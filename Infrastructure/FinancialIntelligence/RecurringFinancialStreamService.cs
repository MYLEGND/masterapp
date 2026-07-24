using System.Text.Json;
using Domain.Entities.FinancialIntelligence;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.FinancialIntelligence;

internal sealed class RecurringFinancialStreamService : IRecurringFinancialStreamService
{
    private const int MinimumOccurrenceCount = 3;
    private readonly MasterAppDbContext _db;
    private readonly ILogger<RecurringFinancialStreamService> _logger;

    private sealed record RecurringCandidate(
        Guid FinancialDataConnectionId,
        Guid ImportedFinancialAccountId,
        string StreamKey,
        string NormalizedMerchantKey,
        string DisplayName,
        string Cadence,
        long AverageAmountCents,
        DateTime NextExpectedDateUtc,
        decimal Confidence,
        string EvidenceJson,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc);

    private sealed record StreamGroupKey(
        Guid FinancialDataConnectionId,
        Guid ImportedFinancialAccountId,
        string MerchantKey);

    public RecurringFinancialStreamService(
        MasterAppDbContext db,
        ILogger<RecurringFinancialStreamService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RefreshRecurringFinancialStreamsResult> RefreshAsync(
        RefreshRecurringFinancialStreamsCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ClientProfileId == Guid.Empty)
            return Failure("STREAM_SCOPE_INVALID", "A client profile is required.");

        if (!await FinancialIntelligenceScope.ClientProfileExistsAsync(_db, command.ClientProfileId, cancellationToken))
            return Failure("CLIENT_PROFILE_NOT_FOUND", "The requested client profile was not found.");

        if (command.FinancialDataConnectionId.HasValue &&
            command.FinancialDataConnectionId.Value != Guid.Empty)
        {
            var connection = await FinancialIntelligenceScope.FindConnectionAsync(
                _db,
                command.ClientProfileId,
                command.FinancialDataConnectionId.Value,
                asNoTracking: true,
                cancellationToken);
            if (connection is null)
                return Failure("CONNECTION_NOT_FOUND", "The requested financial connection was not found for this client.");
        }

        var transactionQuery = _db.ImportedFinancialTransactions
            .AsNoTracking()
            .Where(x =>
                x.ClientProfileId == command.ClientProfileId &&
                !x.IsPending &&
                !x.IsRemoved);
        if (command.FinancialDataConnectionId.HasValue && command.FinancialDataConnectionId.Value != Guid.Empty)
        {
            var connectionId = command.FinancialDataConnectionId.Value;
            transactionQuery = transactionQuery.Where(x => x.FinancialDataConnectionId == connectionId);
        }

        var transactions = await transactionQuery
            .OrderBy(x => x.PostedUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var candidates = BuildCandidates(transactions);
        var candidateKeys = candidates.Select(x => x.StreamKey).ToHashSet(StringComparer.Ordinal);

        var streamQuery = _db.RecurringFinancialStreams
            .Where(x => x.ClientProfileId == command.ClientProfileId);
        if (command.FinancialDataConnectionId.HasValue && command.FinancialDataConnectionId.Value != Guid.Empty)
        {
            var connectionId = command.FinancialDataConnectionId.Value;
            streamQuery = streamQuery.Where(x => x.FinancialDataConnectionId == connectionId);
        }

        var existingStreams = await streamQuery.ToListAsync(cancellationToken);
        var streamsByKey = existingStreams.ToDictionary(x => x.StreamKey, StringComparer.Ordinal);
        var nowUtc = DateTime.UtcNow;
        var createdCount = 0;
        var updatedCount = 0;
        var inactivatedCount = 0;

        foreach (var candidate in candidates)
        {
            if (!streamsByKey.TryGetValue(candidate.StreamKey, out var stream))
            {
                stream = new RecurringFinancialStream
                {
                    ClientProfileId = command.ClientProfileId,
                    FinancialDataConnectionId = candidate.FinancialDataConnectionId,
                    ImportedFinancialAccountId = candidate.ImportedFinancialAccountId,
                    StreamKey = candidate.StreamKey,
                    CreatedUtc = nowUtc
                };
                _db.RecurringFinancialStreams.Add(stream);
                streamsByKey.Add(candidate.StreamKey, stream);
                createdCount += 1;
            }
            else
            {
                updatedCount += 1;
            }

            stream.FinancialDataConnectionId = candidate.FinancialDataConnectionId;
            stream.ImportedFinancialAccountId = candidate.ImportedFinancialAccountId;
            stream.NormalizedMerchantKey = candidate.NormalizedMerchantKey;
            stream.DisplayName = candidate.DisplayName;
            stream.Cadence = candidate.Cadence;
            stream.AverageAmountCents = candidate.AverageAmountCents;
            stream.NextExpectedDateUtc = candidate.NextExpectedDateUtc;
            stream.Status = string.Equals(stream.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)
                ? "Confirmed"
                : "Candidate";
            stream.Confidence = candidate.Confidence;
            stream.EvidenceJson = candidate.EvidenceJson;
            stream.FirstSeenUtc = candidate.FirstSeenUtc;
            stream.LastSeenUtc = candidate.LastSeenUtc;
            stream.UpdatedUtc = nowUtc;
        }

        foreach (var stream in existingStreams.Where(x =>
                     !candidateKeys.Contains(x.StreamKey) &&
                     string.Equals(x.Status, "Candidate", StringComparison.OrdinalIgnoreCase)))
        {
            stream.Status = "Inactive";
            stream.UpdatedUtc = nowUtc;
            inactivatedCount += 1;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "Recurring stream refresh save failed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId}",
                command.ClientProfileId,
                command.FinancialDataConnectionId);
            return Failure("STREAM_SAVE_FAILED", "Recurring financial streams could not be saved.");
        }

        var refreshedStreams = streamsByKey.Values
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.StreamKey)
            .ToList();

        _logger.LogInformation(
            "Recurring stream refresh completed. ClientProfileId={ClientProfileId} ConnectionId={ConnectionId} Created={CreatedCount} Updated={UpdatedCount} Inactivated={InactivatedCount}",
            command.ClientProfileId,
            command.FinancialDataConnectionId,
            createdCount,
            updatedCount,
            inactivatedCount);

        return new RefreshRecurringFinancialStreamsResult(
            true,
            null,
            null,
            createdCount,
            updatedCount,
            inactivatedCount,
            refreshedStreams);
    }

    private static IReadOnlyList<RecurringCandidate> BuildCandidates(
        IReadOnlyCollection<ImportedFinancialTransaction> transactions)
    {
        return transactions
            .GroupBy(x => new StreamGroupKey(
                x.FinancialDataConnectionId,
                x.ImportedFinancialAccountId,
                NormalizeMerchantKey(x.OriginalMerchantName ?? x.OriginalName)))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key.MerchantKey))
            .Select(BuildCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.StreamKey)
            .ToList();
    }

    private static RecurringCandidate? BuildCandidate(
        IGrouping<StreamGroupKey, ImportedFinancialTransaction> group)
    {
        var occurrences = group
            .OrderBy(x => x.PostedUtc)
            .ThenBy(x => x.Id)
            .ToList();
        var occurrenceDates = occurrences
            .Select(x => x.PostedUtc.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (occurrenceDates.Count < MinimumOccurrenceCount)
            return null;

        var intervals = occurrenceDates
            .Zip(occurrenceDates.Skip(1), (previous, current) => (current - previous).Days)
            .Where(days => days > 0)
            .ToList();
        if (intervals.Count < MinimumOccurrenceCount - 1)
            return null;

        var cadence = ResolveCadence(intervals, out var cadenceDays, out var intervalConsistency);
        if (cadence is null)
            return null;

        var amounts = occurrences.Select(x => x.AmountCents).ToList();
        var averageAmount = (long)decimal.Truncate(amounts.Aggregate(0m, (sum, amount) => sum + amount) / amounts.Count);
        var amountConsistency = ResolveAmountConsistency(amounts, averageAmount);
        var confidence = Math.Round(Math.Min(0.99m, 0.45m + intervalConsistency * 0.35m + amountConsistency * 0.20m), 4);
        var displayName = occurrences
            .Select(x => NormalizeDisplayName(x.OriginalMerchantName) ?? NormalizeDisplayName(x.OriginalName))
            .FirstOrDefault(x => x is not null) ?? group.Key.MerchantKey;
        var firstSeen = occurrences[0].PostedUtc;
        var lastSeen = occurrences[^1].PostedUtc;
        var evidence = JsonSerializer.Serialize(new
        {
            occurrenceCount = occurrences.Count,
            distinctOccurrenceDateCount = occurrenceDates.Count,
            intervalDays = intervals,
            averageIntervalDays = Math.Round(intervals.Average(), 2),
            averageAmountCents = averageAmount,
            intervalConsistency,
            amountConsistency
        });

        return new RecurringCandidate(
            group.Key.FinancialDataConnectionId,
            group.Key.ImportedFinancialAccountId,
            BuildStreamKey(group.Key.ImportedFinancialAccountId, group.Key.MerchantKey),
            group.Key.MerchantKey,
            displayName,
            cadence,
            averageAmount,
            lastSeen.AddDays(cadenceDays),
            confidence,
            evidence,
            firstSeen,
            lastSeen);
    }

    private static string? ResolveCadence(
        IReadOnlyCollection<int> intervals,
        out int cadenceDays,
        out decimal intervalConsistency)
    {
        var orderedIntervals = intervals.OrderBy(x => x).ToList();
        var median = orderedIntervals[orderedIntervals.Count / 2];
        var candidate = median switch
        {
            >= 6 and <= 8 => (Name: "Weekly", Days: 7, Tolerance: 2),
            >= 12 and <= 16 => (Name: "Biweekly", Days: 14, Tolerance: 3),
            >= 27 and <= 32 => (Name: "Monthly", Days: 30, Tolerance: 4),
            _ => default((string Name, int Days, int Tolerance)?)
        };

        if (!candidate.HasValue)
        {
            cadenceDays = 0;
            intervalConsistency = 0m;
            return null;
        }

        var matchingIntervals = intervals.Count(days => Math.Abs(days - candidate.Value.Days) <= candidate.Value.Tolerance);
        intervalConsistency = decimal.Divide(matchingIntervals, intervals.Count);
        if (intervalConsistency < 0.67m)
        {
            cadenceDays = 0;
            return null;
        }

        cadenceDays = candidate.Value.Days;
        return candidate.Value.Name;
    }

    private static decimal ResolveAmountConsistency(IReadOnlyCollection<long> amounts, long averageAmount)
    {
        var baseline = Math.Max(1m, Math.Abs(averageAmount));
        var matchingAmounts = amounts.Count(amount =>
            decimal.Abs((decimal)amount - averageAmount) <= baseline * 0.20m);
        return decimal.Divide(matchingAmounts, amounts.Count);
    }

    private static string BuildStreamKey(Guid accountId, string normalizedMerchantKey) =>
        $"{accountId:N}:{normalizedMerchantKey}";

    private static string NormalizeMerchantKey(string? value)
    {
        var source = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var characters = source
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();
        var normalized = string.Join(' ', new string(characters)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static string? NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static RefreshRecurringFinancialStreamsResult Failure(string errorCode, string summary) =>
        new(false, errorCode, summary, 0, 0, 0, Array.Empty<RecurringFinancialStream>());
}
