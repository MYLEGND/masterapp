
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Models;

namespace AgentPortal.Services;

public sealed class ProductionTotals
{
    public decimal Submitted { get; set; }
    public decimal Issued { get; set; }
    public decimal Paid { get; set; }
    public int CountSubmitted { get; set; }
    public int CountIssued { get; set; }
    public int CountPaid { get; set; }
    public decimal Personal { get; set; }
    public int CountPersonal { get; set; }
}

internal enum ResolvedProductionBucket
{
    Lead = 0,
    Client = 1
}

internal sealed record ResolvedProductionRow(
    string AgentUserId,
    ResolvedProductionBucket Bucket,
    ProductionStatus Status,
    decimal Amount,
    decimal PersonalAmount,
    DateTime UpdatedUtc);

internal sealed record ProductionContactSnapshot(
    ProductionStatus Status,
    decimal Amount,
    decimal Personal,
    DateTime UpdatedUtc)
{
    public decimal Submitted => Status == ProductionStatus.Submitted ? Amount : 0m;
    public decimal Issued => Status == ProductionStatus.Issued ? Amount : 0m;
    public decimal Paid => Status == ProductionStatus.Paid ? Amount : 0m;
}

/// <summary>
/// Central production/read/write surface. Status buckets are mutually exclusive by current Status.
/// </summary>
public class ProductionService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<ProductionService> _logger;
    // Delete all production records for an agent and side (e.g., all leads)
    public async Task DeleteAllForAgentAsync(string agentUserId, ProductionSide side, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var toDelete = await _db.ProductionRecords
            .Where(p => p.AgentUserId == normAgent && p.Side == side)
            .ToListAsync(ct);
        if (toDelete.Count == 0) return;
        _db.ProductionRecords.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production RESET ALL by {Agent} for side {Side}", agentUserId, side);
    }

    public ProductionService(MasterAppDbContext db, ILogger<ProductionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();
    private static string OwnershipKey(string? agentUserId, string? subjectId) => $"{Norm(agentUserId)}|{Norm(subjectId)}";
    private static bool IsClientRecordType(string? recordType)
        => string.Equals(recordType, "Client", StringComparison.OrdinalIgnoreCase)
           || string.Equals(recordType, "BusinessClient", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveClientRecordType(string? clientUserId, string? crmNotes)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(crmNotes);
        var normalized = ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType, defaultToLead: false);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var fromStage = ClientCrmMetaSerializer.NormalizeRecordType(meta.PipelineStage, defaultToLead: false);
        if (!string.IsNullOrWhiteSpace(fromStage))
            return fromStage;

        return null;
    }

    private static ResolvedProductionBucket ResolveBucket(
        string agentUserId,
        ProductionSide side,
        string? leadId,
        string? clientUserId,
        IReadOnlySet<string> validLeadOwnership,
        IReadOnlyDictionary<string, string> clientRecordTypes)
    {
        var clientKey = OwnershipKey(agentUserId, clientUserId);
        if (clientRecordTypes.TryGetValue(clientKey, out var recordType))
            return IsClientRecordType(recordType) ? ResolvedProductionBucket.Client : ResolvedProductionBucket.Lead;

        var leadKey = OwnershipKey(agentUserId, leadId);
        if (!string.IsNullOrWhiteSpace(leadId) && validLeadOwnership.Contains(leadKey))
            return ResolvedProductionBucket.Lead;

        return side == ProductionSide.Client ? ResolvedProductionBucket.Client : ResolvedProductionBucket.Lead;
    }

    private async Task<(HashSet<string> ValidLeadOwnership, Dictionary<string, string> ClientRecordTypes)> LoadProductionOwnershipMapsAsync(CancellationToken ct = default)
    {
        var leadOwnership = (await _db.WorkstationLeadProfiles
                .AsNoTracking()
                .Where(x => x.AgentUserId != null && x.LeadId != null)
                .Select(x => new { x.AgentUserId, x.LeadId })
                .ToListAsync(ct))
            .Select(x => OwnershipKey(x.AgentUserId, x.LeadId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var clientRows = await (from ac in _db.AgentClients.AsNoTracking()
                                join cp in _db.ClientProfiles.AsNoTracking() on ac.ClientUserId equals cp.ClientUserId
                                select new
                                {
                                    ac.AgentUserId,
                                    cp.ClientUserId,
                                    cp.CrmNotes
                                })
            .ToListAsync(ct);

        var clientRecordTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in clientRows)
        {
            var recordType = ResolveClientRecordType(row.ClientUserId, row.CrmNotes);
            if (!string.IsNullOrWhiteSpace(recordType))
                clientRecordTypes[OwnershipKey(row.AgentUserId, row.ClientUserId)] = recordType;
        }

        return (leadOwnership, clientRecordTypes);
    }

    private async Task<List<ResolvedProductionRow>> LoadResolvedProductionRowsAsync(
        DateTime? startUtc = null,
        DateTime? endUtc = null,
        CancellationToken ct = default)
    {
        var (validLeadOwnership, clientRecordTypes) = await LoadProductionOwnershipMapsAsync(ct);

        var query = _db.ProductionRecords.AsNoTracking();
        if (startUtc.HasValue)
            query = query.Where(p => p.UpdatedUtc >= startUtc.Value);
        if (endUtc.HasValue)
            query = query.Where(p => p.UpdatedUtc <= endUtc.Value);

        var rows = await query
            .Select(p => new
            {
                p.AgentUserId,
                p.Side,
                p.Status,
                p.Amount,
                p.PersonalAmount,
                p.UpdatedUtc,
                p.LeadId,
                p.ClientUserId
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new ResolvedProductionRow(
                Norm(row.AgentUserId),
                ResolveBucket(row.AgentUserId, row.Side, row.LeadId, row.ClientUserId, validLeadOwnership, clientRecordTypes),
                row.Status,
                row.Amount,
                row.PersonalAmount,
                row.UpdatedUtc))
            .ToList();
    }

    private static void Accumulate(ProductionStatus status, decimal amount, ProductionTotals totals)
    {
        switch (status)
        {
            case ProductionStatus.Submitted:
                totals.Submitted += amount; totals.CountSubmitted += 1; break;
            case ProductionStatus.Issued:
                totals.Issued += amount; totals.CountIssued += 1; break;
            case ProductionStatus.Paid:
                totals.Paid += amount; totals.CountPaid += 1; break;
        }
    }

    private static void Accumulate(ProductionContactSnapshot snapshot, ProductionTotals totals)
    {
        switch (snapshot.Status)
        {
            case ProductionStatus.Submitted:
                totals.Submitted += snapshot.Amount;
                totals.CountSubmitted += 1;
                break;
            case ProductionStatus.Issued:
                totals.Issued += snapshot.Amount;
                totals.CountIssued += 1;
                break;
            case ProductionStatus.Paid:
                totals.Paid += snapshot.Amount;
                totals.CountPaid += 1;
                break;
        }

        totals.Personal += snapshot.Personal;
        if (snapshot.Personal > 0)
            totals.CountPersonal += 1;
    }

    private static Dictionary<string, ProductionContactSnapshot> BuildCurrentContactSnapshots(
        IEnumerable<ProductionRecord> records,
        Func<ProductionRecord, string?> contactSelector)
    {
        return records
            .Select(record => new
            {
                Record = record,
                ContactId = (contactSelector(record) ?? string.Empty).Trim()
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ContactId))
            .GroupBy(x => x.ContactId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.Select(x => x.Record)
                        .OrderByDescending(x => x.UpdatedUtc)
                        .ThenByDescending(x => x.CreatedUtc)
                        .ThenByDescending(x => x.Id)
                        .First();

                    return new ProductionContactSnapshot(
                        latest.Status,
                        latest.Amount,
                        latest.PersonalAmount,
                        latest.UpdatedUtc);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    internal async Task<Dictionary<string, ProductionContactSnapshot>> GetContactSnapshotsAsync(
        string agentUserId,
        ProductionSide side,
        IEnumerable<string> contactIds,
        CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var ids = contactIds
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, ProductionContactSnapshot>(StringComparer.OrdinalIgnoreCase);

        var query = _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side);

        if (side == ProductionSide.Lead)
            query = query.Where(p => p.LeadId != null && ids.Contains(p.LeadId));
        else
            query = query.Where(p => p.ClientUserId != null && ids.Contains(p.ClientUserId));

        var rows = await query.ToListAsync(ct);
        return BuildCurrentContactSnapshots(rows, side == ProductionSide.Lead
            ? static p => p.LeadId
            : static p => p.ClientUserId);
    }

    public async Task UpsertAsync(string actorUserId, string targetAgentUserId, ProductionSide side, ProductionStatus status, decimal amount, decimal personalAmount, string? leadId, string? clientUserId, string? notes, CancellationToken ct = default)
    {
        if (amount < 0) throw new ArgumentException("Amount cannot be negative.", nameof(amount));
        if (personalAmount < 0) personalAmount = 0;

        if (side == ProductionSide.Lead && string.IsNullOrWhiteSpace(leadId))
            throw new ArgumentException("LeadId is required for lead production.", nameof(leadId));
        if (side == ProductionSide.Client && string.IsNullOrWhiteSpace(clientUserId))
            throw new ArgumentException("ClientUserId is required for client production.", nameof(clientUserId));

        var normAgent = Norm(targetAgentUserId);
        var now = DateTime.UtcNow;

        // Add operation should always create a new production row.
        // Editing/deleting specific rows is handled via UpdateAsync/DeleteAsync using record Id.
        var record = new ProductionRecord
        {
            AgentUserId = normAgent,
            Side = side,
            LeadId = side == ProductionSide.Lead ? leadId : null,
            ClientUserId = side == ProductionSide.Client ? clientUserId : null,
            Status = status,
            Amount = amount,
            PersonalAmount = personalAmount,
            Notes = notes?.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        _db.ProductionRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production add by {Actor} for agent {Agent} side {Side} status {Status} amount {Amount} personal {Personal}", actorUserId, targetAgentUserId, side, status, amount, personalAmount);
    }

    public async Task<List<ProductionRecord>> GetForContactAsync(string agentUserId, ProductionSide side, string contactId, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        return await _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side &&
                        ((side == ProductionSide.Lead && p.LeadId == contactId) ||
                         (side == ProductionSide.Client && p.ClientUserId == contactId)))
            .OrderByDescending(p => p.UpdatedUtc)
            .ToListAsync(ct);
    }

    public async Task<ProductionTotals> GetAgentTotalsAsync(string agentUserId, ProductionSide side, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var rows = await _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side)
            .ToListAsync(ct);

        var totals = new ProductionTotals();
        var currentSnapshots = BuildCurrentContactSnapshots(rows, side == ProductionSide.Lead
            ? static p => p.LeadId
            : static p => p.ClientUserId);

        foreach (var snapshot in currentSnapshots.Values)
        {
            Accumulate(snapshot, totals);
        }

        return totals;
    }

    public async Task<List<ProductionRecord>> GetHistoryAsync(string agentUserId, ProductionSide side, string? leadId, string? clientUserId, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var query = _db.ProductionRecords.AsNoTracking().Where(p => p.AgentUserId == normAgent && p.Side == side);
        if (side == ProductionSide.Lead)
            query = query.Where(p => p.LeadId == leadId);
        else
            query = query.Where(p => p.ClientUserId == clientUserId);

        return await query.OrderByDescending(p => p.UpdatedUtc).ToListAsync(ct);
    }

    public async Task<ProductionRecord?> GetByIdAsync(string agentUserId, Guid id, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        return await _db.ProductionRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == normAgent, ct);
    }

    public async Task UpdateAsync(string actorUserId, string agentUserId, Guid id, ProductionStatus status, decimal amount, decimal personalAmount, string? notes, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var record = await _db.ProductionRecords.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == normAgent, ct);
        if (record == null) throw new InvalidOperationException("Production record not found or not owned by agent.");
        if (amount < 0) throw new ArgumentException("Amount cannot be negative.", nameof(amount));
        if (personalAmount < 0) personalAmount = 0;

        record.Status = status;
        record.Amount = amount;
        record.PersonalAmount = personalAmount;
        record.Notes = notes?.Trim();
        record.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production updated by {Actor} for agent {Agent} record {Record} status {Status} amount {Amount} personal {Personal}", actorUserId, agentUserId, id, status, amount, personalAmount);
    }

    public async Task<(ProductionTotals Leads, ProductionTotals Clients, Dictionary<string, ProductionTotals> ByAgent)> GetAgencyTotalsAsync(CancellationToken ct = default)
    {
        var rows = await LoadResolvedProductionRowsAsync(ct: ct);

        ProductionTotals leads = new(), clients = new();
        var byAgent = new Dictionary<string, ProductionTotals>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var targetTotals = row.Bucket == ResolvedProductionBucket.Lead ? leads : clients;
            Accumulate(row.Status, row.Amount, targetTotals);
            targetTotals.Personal += row.PersonalAmount;
            if (row.PersonalAmount > 0) targetTotals.CountPersonal += 1;

            if (!byAgent.TryGetValue(row.AgentUserId, out var agentTotals))
            {
                agentTotals = new ProductionTotals();
                byAgent[row.AgentUserId] = agentTotals;
            }

            Accumulate(row.Status, row.Amount, agentTotals);
            agentTotals.Personal += row.PersonalAmount;
            if (row.PersonalAmount > 0) agentTotals.CountPersonal += 1;
        }

        return (leads, clients, byAgent);
    }

    private static void Apply(dynamic row, ProductionTotals totals)
    {
        var status = (ProductionStatus)row.Status;
        var amount = (decimal)row.Amount;
        var count = (int)row.Count;
        switch (status)
        {
            case ProductionStatus.Submitted:
                totals.Submitted += amount; totals.CountSubmitted += count; break;
            case ProductionStatus.Issued:
                totals.Issued += amount; totals.CountIssued += count; break;
            case ProductionStatus.Paid:
                totals.Paid += amount; totals.CountPaid += count; break;
        }
    }

    public async Task DeleteAsync(string actorUserId, string agentUserId, Guid id, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var record = await _db.ProductionRecords.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == normAgent, ct);
        if (record == null) throw new InvalidOperationException("Production record not found or not owned by agent.");

        _db.ProductionRecords.Remove(record);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production deleted by {Actor} for agent {Agent} record {Record}", actorUserId, agentUserId, id);
    }

    public async Task DeleteForContactAsync(string actorUserId, string agentUserId, ProductionSide side, string? leadId, string? clientUserId, CancellationToken ct = default)
    {
        var normAgent = Norm(agentUserId);
        var toDelete = await _db.ProductionRecords
            .Where(p => p.AgentUserId == normAgent
                        && p.Side == side
                        && ((side == ProductionSide.Lead && p.LeadId == leadId)
                            || (side == ProductionSide.Client && p.ClientUserId == clientUserId)))
            .ToListAsync(ct);

        if (toDelete.Count == 0) return;

        _db.ProductionRecords.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production reset by {Actor} for agent {Agent} side {Side} contact lead:{LeadId} client:{ClientUserId}", actorUserId, agentUserId, side, leadId, clientUserId);
    }

    /// <summary>
    /// Monthly agent totals for the specified year up to the provided month (inclusive).
    /// Grouping uses the supplied local timezone to avoid UTC month boundary drift.
    /// Returned dictionary: month (1-12) -> (agentUserId -> totals across all sides).
    /// </summary>
    public async Task<Dictionary<int, Dictionary<string, ProductionTotals>>> GetMonthlyAgentTotalsAsync(
        TimeZoneInfo localTz,
        int year,
        int maxMonthInclusive,
        CancellationToken ct = default)
    {
        var safeMaxMonth = Math.Clamp(maxMonthInclusive, 1, 12);
        var startLocal = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(year, safeMaxMonth, DateTime.DaysInMonth(year, safeMaxMonth), 23, 59, 59, 999, DateTimeKind.Unspecified);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, localTz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, localTz);

        var rows = await _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.UpdatedUtc >= startUtc && p.UpdatedUtc <= endUtc)
            .Select(p => new { p.AgentUserId, p.Status, p.Amount, p.PersonalAmount, p.UpdatedUtc, Count = 1 })
            .ToListAsync(ct);

        var result = new Dictionary<int, Dictionary<string, ProductionTotals>>();

        foreach (var row in rows)
        {
            var month = TimeZoneInfo.ConvertTimeFromUtc(row.UpdatedUtc, localTz).Month;
            if (!result.TryGetValue(month, out var agentMap))
            {
                agentMap = new Dictionary<string, ProductionTotals>(StringComparer.OrdinalIgnoreCase);
                result[month] = agentMap;
            }

            var agentKey = Norm(row.AgentUserId);
            if (!agentMap.TryGetValue(agentKey, out var totals))
            {
                totals = new ProductionTotals();
                agentMap[agentKey] = totals;
            }

            Apply(row, totals);
            totals.Personal += row.PersonalAmount;
            if (row.PersonalAmount > 0) totals.CountPersonal += 1;
        }

        return result;
    }

    /// <summary>
    /// Monthly -> Producer -> daily/weekly breakdown using local timezone to avoid UTC boundary drift.
    /// Weeks are Monday-start, local time.
    /// </summary>
    public async Task<List<MonthlyProducerVm>> GetMonthlyProducerBreakdownAsync(
        TimeZoneInfo localTz,
        int year,
        int maxMonthInclusive,
        CancellationToken ct = default)
    {
        var safeMaxMonth = Math.Clamp(maxMonthInclusive, 1, 12);
        var startLocal = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(year, safeMaxMonth, DateTime.DaysInMonth(year, safeMaxMonth), 23, 59, 59, 999, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, localTz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, localTz);

        var records = await LoadResolvedProductionRowsAsync(startUtc, endUtc, ct);

        var months = new Dictionary<int, MonthlyProducerVm>();

        foreach (var r in records)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(r.UpdatedUtc, localTz);
            var month = local.Month;
            if (!months.TryGetValue(month, out var monthVm))
            {
                monthVm = new MonthlyProducerVm { Month = month };
                months[month] = monthVm;
            }

            var agentKey = Norm(r.AgentUserId);
            var producer = monthVm.Producers.FirstOrDefault(p => p.AgentUserId.Equals(agentKey, StringComparison.OrdinalIgnoreCase));
            if (producer == null)
            {
                producer = new ProducerMonthVm { AgentUserId = agentKey };
                monthVm.Producers.Add(producer);
            }

            Accumulate(r.Status, r.Amount, producer.Totals);
            producer.Totals.Personal += r.PersonalAmount;
            if (r.PersonalAmount > 0) producer.Totals.CountPersonal += 1;

            var sideTotals = r.Bucket == ResolvedProductionBucket.Lead ? producer.LeadsTotals : producer.ClientsTotals;
            Accumulate(r.Status, r.Amount, sideTotals);
            sideTotals.Personal += r.PersonalAmount;
            if (r.PersonalAmount > 0) sideTotals.CountPersonal += 1;

            // daily
            var dayKey = DateOnly.FromDateTime(local);
            var daily = producer.Daily.FirstOrDefault(d => d.Date == dayKey);
            if (daily == null)
            {
                daily = new DailyBreakdownVm { Date = dayKey };
                producer.Daily.Add(daily);
            }
            Accumulate(r.Status, r.Amount, daily.Totals);

            // weekly (Monday start)
            var weekStart = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)); // Monday = 0
            var weekKey = DateOnly.FromDateTime(weekStart);
            var weekly = producer.Weekly.FirstOrDefault(w => w.WeekStart == weekKey);
            if (weekly == null)
            {
                weekly = new WeeklyBreakdownVm { WeekStart = weekKey };
                producer.Weekly.Add(weekly);
            }
            Accumulate(r.Status, r.Amount, weekly.Totals);
        }

        // sort daily/weekly chronologically; producers by Paid->Issued->Submitted; months ordering done by caller
        foreach (var m in months.Values)
        {
            foreach (var p in m.Producers)
            {
                p.Daily = p.Daily.OrderBy(d => d.Date).ToList();
                p.Weekly = p.Weekly.OrderBy(w => w.WeekStart).ToList();
            }
            m.Producers = m.Producers
                .OrderByDescending(p => p.Totals.Paid)
                .ThenByDescending(p => p.Totals.Issued)
                .ThenByDescending(p => p.Totals.Submitted)
                .ToList();
        }

        return months.Values.ToList();
    }
}
