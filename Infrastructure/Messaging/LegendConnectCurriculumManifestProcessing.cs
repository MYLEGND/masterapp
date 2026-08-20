using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed class LegendConnectCurriculumManifestProcessor
{
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    private readonly MasterAppDbContext _db;
    private readonly LegendConnectCurriculumService _curriculum;
    private readonly ILogger<LegendConnectCurriculumManifestProcessor> _logger;

    public LegendConnectCurriculumManifestProcessor(
        MasterAppDbContext db,
        LegendConnectCurriculumService curriculum,
        ILogger<LegendConnectCurriculumManifestProcessor> logger)
    {
        _db = db;
        _curriculum = curriculum;
        _logger = logger;
    }

    internal async Task<int> ProcessPendingAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var evaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        var candidateIds = await _db.Set<LegendCurriculumManifestWorkItem>()
            .AsNoTracking()
            .Where(item =>
                item.ProcessingState == "Pending" ||
                (item.ProcessingState == "Processing" &&
                 item.LeaseExpiresUtc != null &&
                 item.LeaseExpiresUtc < now) ||
                (item.ProcessingState == "Completed" &&
                 item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion))
            // Preserve prompt handling for new work. Historical capability
            // replays are still durable and bounded, but never starve newly
            // accepted Founder curriculum.
            .OrderBy(item => item.ProcessingState == "Completed")
            .ThenBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Take(Math.Clamp(take, 1, 4))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var id in candidateIds)
        {
            var claimed = await TryClaimAsync(id, cancellationToken);
            if (claimed is null)
                continue;

            await ProcessOneFamilyAsync(claimed, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task<LegendCurriculumManifestWorkItem?> TryClaimAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leaseExpires = now.Add(LeaseDuration);
        var evaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current;

        if (_db.Database.IsRelational())
        {
            var updated = await _db.Set<LegendCurriculumManifestWorkItem>()
                .Where(item => item.Id == id &&
                    (item.ProcessingState == "Pending" ||
                     (item.ProcessingState == "Processing" &&
                      item.LeaseExpiresUtc != null &&
                      item.LeaseExpiresUtc < now) ||
                     (item.ProcessingState == "Completed" &&
                      item.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProcessingState, "Processing")
                    // A completed historical work item is the only case that
                    // restarts at family zero. Interrupted normal/replay work
                    // retains its exact durable cursor and target version.
                    .SetProperty(item => item.NextFamilyIndex,
                        item => item.ProcessingState == "Completed" ? 0 : item.NextFamilyIndex)
                    .SetProperty(item => item.TargetLanguageIntelligenceEvaluatorVersion,
                        item => item.ProcessingState == "Completed"
                            ? evaluatorVersion
                            : item.TargetLanguageIntelligenceEvaluatorVersion > 0
                                ? item.TargetLanguageIntelligenceEvaluatorVersion
                                : evaluatorVersion)
                    .SetProperty(item => item.LeaseExpiresUtc, leaseExpires)
                    .SetProperty(item => item.CompletedUtc,
                        item => item.ProcessingState == "Completed" ? null : item.CompletedUtc)
                    .SetProperty(item => item.UpdatedUtc, now),
                    cancellationToken);
            if (updated != 1)
                return null;

            _db.ChangeTracker.Clear();
            return await _db.Set<LegendCurriculumManifestWorkItem>()
                .SingleAsync(item => item.Id == id, cancellationToken);
        }

        var item = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleOrDefaultAsync(candidate => candidate.Id == id &&
                (candidate.ProcessingState == "Pending" ||
                 (candidate.ProcessingState == "Processing" &&
                  candidate.LeaseExpiresUtc != null &&
                  candidate.LeaseExpiresUtc < now) ||
                 (candidate.ProcessingState == "Completed" &&
                  candidate.CompletedLanguageIntelligenceEvaluatorVersion < evaluatorVersion)),
                cancellationToken);
        if (item is null)
            return null;

        if (item.ProcessingState == "Completed")
        {
            item.NextFamilyIndex = 0;
            item.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
            item.CompletedUtc = null;
        }
        else if (item.TargetLanguageIntelligenceEvaluatorVersion <= 0)
        {
            item.TargetLanguageIntelligenceEvaluatorVersion = evaluatorVersion;
        }
        item.ProcessingState = "Processing";
        item.LeaseExpiresUtc = leaseExpires;
        item.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return item;
    }

    private async Task ProcessOneFamilyAsync(
        LegendCurriculumManifestWorkItem work,
        CancellationToken cancellationToken)
    {
        LegendConnectCurriculumManifestSubmission? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(work.PayloadJson);
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(
                work.Id,
                "curriculum_manifest_payload_invalid",
                exception.Message,
                cancellationToken);
            return;
        }

        var families = manifest?.Families?.ToArray() ?? [];
        if (families.Length != work.FamilyCount || families.Length == 0)
        {
            await MarkFailedAsync(
                work.Id,
                "curriculum_manifest_payload_mismatch",
                "The durable manifest payload no longer matches its accepted family count.",
                cancellationToken);
            return;
        }

        if (work.NextFamilyIndex >= families.Length)
        {
            await MarkCompletedAsync(work.Id, cancellationToken);
            return;
        }

        var familyIndex = work.NextFamilyIndex;
        var family = families[familyIndex];

        try
        {
            // Canonical learning behavior is unchanged. Only execution timing
            // changed: one bounded family outside the Founder HTTP request.
            var result = await _curriculum.SubmitFounderEnglishBatchAsync(
                family,
                cancellationToken);

            if (!result.Succeeded)
            {
                await MarkFailedAsync(
                    work.Id,
                    result.ErrorCode ?? "curriculum_family_processing_rejected",
                    result.Message ?? $"Family {family.FamilyKey} was rejected by the canonical curriculum authority.",
                    cancellationToken);
                return;
            }

            _db.ChangeTracker.Clear();
            var current = await _db.Set<LegendCurriculumManifestWorkItem>()
                .SingleAsync(item => item.Id == work.Id, cancellationToken);
            current.NextFamilyIndex = familyIndex + 1;
            current.AttemptCount = 0;
            current.LastErrorCode = null;
            current.LastErrorMessage = null;
            current.LeaseExpiresUtc = null;
            current.UpdatedUtc = DateTime.UtcNow;

            var isCompleted = current.NextFamilyIndex >= current.FamilyCount;
            current.ProcessingState = isCompleted ? "Completed" : "Pending";
            current.CompletedUtc = isCompleted ? DateTime.UtcNow : null;
            if (isCompleted)
            {
                current.CompletedLanguageIntelligenceEvaluatorVersion =
                    current.TargetLanguageIntelligenceEvaluatorVersion > 0
                        ? current.TargetLanguageIntelligenceEvaluatorVersion
                        : LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
            }

            _db.Set<LegendConnectKnowledgeAuditEntry>().Add(new LegendConnectKnowledgeAuditEntry
            {
                Id = Guid.NewGuid(),
                FounderUserId = current.FounderUserId,
                Action = "FounderCurriculumFamilyProcessed",
                Result = result.DuplicatePrevented ? "CanonicalReuse" : "Succeeded",
                LanguageCode = "en",
                Detail = Truncate(
                    $"Manifest {current.ManifestHash[..12]} family {familyIndex + 1}/{current.FamilyCount}: {family.FamilyKey}. " +
                    $"Evaluator v{current.TargetLanguageIntelligenceEvaluatorVersion}. " +
                    (result.Message ?? "Canonical curriculum processing completed."),
                    500),
                OccurredUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Legend Connect curriculum manifest family processing failed. WorkId={WorkId} FamilyIndex={FamilyIndex}",
                work.Id,
                familyIndex);

            await MarkRetryableAsync(
                work.Id,
                exception.GetType().Name,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task MarkRetryableAsync(
        Guid id,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var work = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == id, cancellationToken);

        work.AttemptCount++;
        work.LastErrorCode = Truncate(errorCode, 120);
        work.LastErrorMessage = Truncate(message, 1000);
        work.LeaseExpiresUtc = null;
        work.UpdatedUtc = DateTime.UtcNow;
        work.ProcessingState = work.AttemptCount >= MaximumAttempts ? "Failed" : "Pending";
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(
        Guid id,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var work = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == id, cancellationToken);
        work.ProcessingState = "Failed";
        work.LastErrorCode = Truncate(errorCode, 120);
        work.LastErrorMessage = Truncate(message, 1000);
        work.LeaseExpiresUtc = null;
        work.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkCompletedAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var work = await _db.Set<LegendCurriculumManifestWorkItem>()
            .SingleAsync(item => item.Id == id, cancellationToken);
        work.NextFamilyIndex = work.FamilyCount;
        work.ProcessingState = "Completed";
        work.CompletedLanguageIntelligenceEvaluatorVersion =
            work.TargetLanguageIntelligenceEvaluatorVersion > 0
                ? work.TargetLanguageIntelligenceEvaluatorVersion
                : LegendConnectLanguageIntelligenceEvaluatorVersion.Current;
        work.LeaseExpiresUtc = null;
        work.CompletedUtc = DateTime.UtcNow;
        work.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}

public sealed class LegendConnectCurriculumManifestHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegendConnectCurriculumManifestHostedService> _logger;

    public LegendConnectCurriculumManifestHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<LegendConnectCurriculumManifestHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                var curriculum = scope.ServiceProvider.GetRequiredService<LegendConnectCurriculumService>();
                var processorLogger = scope.ServiceProvider
                    .GetRequiredService<ILogger<LegendConnectCurriculumManifestProcessor>>();
                var processor = new LegendConnectCurriculumManifestProcessor(
                    db,
                    curriculum,
                    processorLogger);

                processed = await processor.ProcessPendingAsync(1, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Legend Connect curriculum manifest background processing cycle failed.");
            }

            await Task.Delay(
                processed > 0 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5),
                stoppingToken);
        }
    }
}
