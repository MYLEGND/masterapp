
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

    public async Task UpsertAsync(string actorUserId, string targetAgentUserId, ProductionSide side, ProductionStatus status, decimal amount, decimal personalAmount, string? leadId, string? clientUserId, string? notes, CancellationToken ct = default)
    {
        if (amount < 0) throw new ArgumentException("Amount cannot be negative.", nameof(amount));
        if (personalAmount < 0) personalAmount = 0;

        var normAgent = Norm(targetAgentUserId);
        var query = _db.ProductionRecords
            .Where(p =>
                p.AgentUserId == normAgent &&
                p.Side == side &&
                ((side == ProductionSide.Lead && p.LeadId == leadId) ||
                 (side == ProductionSide.Client && p.ClientUserId == clientUserId)));

        var existing = await query
            .OrderByDescending(p => p.UpdatedUtc)
            .FirstOrDefaultAsync(ct);

        if (existing == null)
        {
            existing = new ProductionRecord
            {
                AgentUserId = normAgent,
                Side = side,
                LeadId = leadId,
                ClientUserId = clientUserId,
                CreatedUtc = DateTime.UtcNow
            };
            _db.ProductionRecords.Add(existing);
        }

        existing.Status = status;
        existing.Amount = amount;
        existing.PersonalAmount = personalAmount;
        existing.Notes = notes?.Trim();
        existing.UpdatedUtc = DateTime.UtcNow;

        // ensure only one record per contact/side to avoid double counting
        var extras = await query.Where(p => p.Id != existing.Id).ToListAsync(ct);
        if (extras.Count > 0)
            _db.ProductionRecords.RemoveRange(extras);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Production upsert by {Actor} for agent {Agent} side {Side} status {Status} amount {Amount} personal {Personal}", actorUserId, targetAgentUserId, side, status, amount, personalAmount);
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
        var grouped = await _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Amount = g.Sum(x => (decimal?)x.Amount) ?? 0, Count = g.Count() })
            .ToListAsync(ct);

        var totals = new ProductionTotals();
        foreach (var g in grouped)
        {
            switch (g.Status)
            {
                case ProductionStatus.Submitted:
                    totals.Submitted = g.Amount; totals.CountSubmitted = g.Count; break;
                case ProductionStatus.Issued:
                    totals.Issued = g.Amount; totals.CountIssued = g.Count; break;
                case ProductionStatus.Paid:
                    totals.Paid = g.Amount; totals.CountPaid = g.Count; break;
            }
        }
        totals.Personal = await _db.ProductionRecords.AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side)
            .SumAsync(p => p.PersonalAmount, ct);
        totals.CountPersonal = await _db.ProductionRecords.AsNoTracking()
            .Where(p => p.AgentUserId == normAgent && p.Side == side && p.PersonalAmount > 0)
            .CountAsync(ct);
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
        var grouped = await _db.ProductionRecords
            .AsNoTracking()
            .GroupBy(p => new { p.AgentUserId, p.Side, p.Status })
            .Select(g => new { g.Key.AgentUserId, g.Key.Side, g.Key.Status, Amount = g.Sum(x => (decimal?)x.Amount) ?? 0, Count = g.Count() })
            .ToListAsync(ct);
        var personalGrouped = await _db.ProductionRecords
            .AsNoTracking()
            .GroupBy(p => new { p.AgentUserId, p.Side })
            .Select(g => new { g.Key.AgentUserId, g.Key.Side, Amount = g.Sum(x => x.PersonalAmount), Count = g.Count(x => x.PersonalAmount > 0) })
            .ToListAsync(ct);

        ProductionTotals leads = new(), clients = new();
        var byAgent = new Dictionary<string, ProductionTotals>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in grouped)
        {
            var targetTotals = row.Side == ProductionSide.Lead ? leads : clients;
            Apply(row, targetTotals);

            if (!byAgent.TryGetValue(row.AgentUserId, out var agentTotals))
            {
                agentTotals = new ProductionTotals();
                byAgent[row.AgentUserId] = agentTotals;
            }
            Apply(row, agentTotals);
        }

        foreach (var p in personalGrouped)
        {
            var targetTotals = p.Side == ProductionSide.Lead ? leads : clients;
            targetTotals.Personal += p.Amount;
            targetTotals.CountPersonal += p.Count;

            if (!byAgent.TryGetValue(p.AgentUserId, out var agentTotals))
            {
                agentTotals = new ProductionTotals();
                byAgent[p.AgentUserId] = agentTotals;
            }
            agentTotals.Personal += p.Amount;
            agentTotals.CountPersonal += p.Count;
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

        var records = await _db.ProductionRecords
            .AsNoTracking()
            .Where(p => p.UpdatedUtc >= startUtc && p.UpdatedUtc <= endUtc)
            .Select(p => new { p.AgentUserId, p.Side, p.Status, p.Amount, p.PersonalAmount, p.UpdatedUtc })
            .ToListAsync(ct);

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

            var sideTotals = r.Side == ProductionSide.Lead ? producer.LeadsTotals : producer.ClientsTotals;
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
