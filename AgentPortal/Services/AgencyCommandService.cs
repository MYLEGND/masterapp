using System;
using System.Linq;
using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

public class AgencyCommandService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgencyCommandService> _logger;
    private readonly ProductionService _production;

    public AgencyCommandService(MasterAppDbContext db, ProductionService production, ILogger<AgencyCommandService> logger)
    {
        _db = db;
        _production = production;
        _logger = logger;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private static string NormalizeStage(string? stage, string? bucket)
    {
        if (!string.IsNullOrWhiteSpace(stage)) return stage.Trim();
        if (!string.IsNullOrWhiteSpace(bucket)) return bucket.Trim();
        return "New";
    }

    private static string BuildDisplayName(string? fullName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(fullName)) return fullName.Trim();
        if (!string.IsNullOrWhiteSpace(email)) return email.Trim();
        return "Agent";
    }

    private static string ResolveClientRecordType(string? clientUserId, string? crmNotes)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(crmNotes);
        var explicitRecordType = ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType, defaultToLead: false);
        if (!string.IsNullOrWhiteSpace(explicitRecordType))
            return explicitRecordType;

        var stage = ClientCrmMetaSerializer.NormalizePipelineStage(meta.PipelineStage);
        if (string.Equals(stage, "BusinessClient", StringComparison.OrdinalIgnoreCase))
            return "BusinessClient";
        if (string.Equals(stage, "Client", StringComparison.OrdinalIgnoreCase))
            return "Client";
        if (Guid.TryParse((clientUserId ?? string.Empty).Trim(), out _))
            return "Client";

        return "Lead";
    }

    private static bool IsClientRecordType(string? recordType)
        => string.Equals(recordType, "Client", StringComparison.OrdinalIgnoreCase)
           || string.Equals(recordType, "BusinessClient", StringComparison.OrdinalIgnoreCase);

    private sealed class AgentDescriptor
    {
        public AgentDescriptor(string agentUserId) => AgentUserId = agentUserId;
        public string AgentUserId { get; }
        public string? Email { get; set; }
        public string? FullName { get; set; }
        public string? Title { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int? DisplayOrder { get; set; }
    }

    private async Task<Dictionary<string, AgentDescriptor>> DiscoverAgentsAsync()
    {
        var map = new Dictionary<string, AgentDescriptor>(StringComparer.OrdinalIgnoreCase);
        static string NormEmail(string? v) => (v ?? "").Trim().ToLowerInvariant();

        void Seed(string? rawId, string? email = null, string? fullName = null, string? title = null, DateTime? updatedUtc = null, int? displayOrder = null)
        {
            var key = Norm(rawId);
            if (string.IsNullOrWhiteSpace(key)) return;

            if (!map.TryGetValue(key, out var descriptor))
            {
                descriptor = new AgentDescriptor(key);
                map[key] = descriptor;
            }

            if (!string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(descriptor.Email))
                descriptor.Email = email.Trim();

            if (!string.IsNullOrWhiteSpace(fullName) && string.IsNullOrWhiteSpace(descriptor.FullName))
                descriptor.FullName = fullName.Trim();

            if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(descriptor.Title))
                descriptor.Title = title.Trim();

            if (updatedUtc.HasValue && updatedUtc.Value > descriptor.UpdatedUtc)
                descriptor.UpdatedUtc = updatedUtc.Value;

            if (displayOrder.HasValue)
                descriptor.DisplayOrder = displayOrder;
        }

        // ============================================================
        // Build agent signals to distinguish real agents from stray logins.
        // ============================================================
        var assistantOids = await _db.AgentAssistants
            .AsNoTracking()
            .Where(a => a.AssistantUserId != null)
            .Select(a => a.AssistantUserId!)
            .ToListAsync();
        var assistantEmails = await _db.AgentAssistants
            .AsNoTracking()
            .Where(a => a.NormalizedEmail != null)
            .Select(a => a.NormalizedEmail!)
            .ToListAsync();

        var agentSignalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSignals(IEnumerable<string?> ids)
        {
            foreach (var id in ids)
            {
                var key = Norm(id);
                if (!string.IsNullOrWhiteSpace(key))
                    agentSignalIds.Add(key);
            }
        }

        AddSignals(await _db.AgentClients.AsNoTracking().Select(x => x.AgentUserId).ToListAsync());
        AddSignals(await _db.WorkstationLeadProfiles.AsNoTracking().Select(x => x.AgentUserId).ToListAsync());
        AddSignals(await _db.ProductionRecords.AsNoTracking().Select(x => x.AgentUserId).ToListAsync());
        var trackingProfiles = await _db.AgentTrackingProfiles.AsNoTracking().ToListAsync();
        AddSignals(trackingProfiles.Select(x => x.AgentUserId));
        AddSignals(await _db.AgentAssistants.AsNoTracking().Select(x => x.ParentAgentUserId).ToListAsync());

        var clientEmails = await _db.ClientProfiles
            .AsNoTracking()
            .Where(c => c.NormalizedEmail != null)
            .Select(c => c.NormalizedEmail!)
            .ToListAsync();

        var internalDomain = OnboardingGuard.OwnerEmail.Split('@').LastOrDefault()?.Trim().ToLowerInvariant();
        var founderEmailNorm = NormEmail(FounderGuard.FounderEmail);

        // Source of truth: AgentProfiles, but we only keep rows that look like real agents.
        var profiles = await _db.AgentProfiles.AsNoTracking().ToListAsync();
        foreach (var p in profiles)
        {
            var oid = Norm(p.AgentUserId);
            var emailNorm = NormEmail(p.NormalizedEmail ?? p.AgentUpn);

            var isAssistant = assistantOids.Any(a => string.Equals(Norm(a), oid, StringComparison.OrdinalIgnoreCase)) ||
                              (!string.IsNullOrWhiteSpace(emailNorm) && assistantEmails.Contains(emailNorm, StringComparer.OrdinalIgnoreCase));
            if (isAssistant) continue;

            var hasSignal = agentSignalIds.Contains(oid);
            var isFounder = !string.IsNullOrWhiteSpace(emailNorm) && emailNorm.Equals(founderEmailNorm, StringComparison.OrdinalIgnoreCase);
            var isInternalDomain = !string.IsNullOrWhiteSpace(internalDomain) &&
                                   !string.IsNullOrWhiteSpace(emailNorm) &&
                                   emailNorm.EndsWith("@" + internalDomain, StringComparison.OrdinalIgnoreCase);
            var isClientOnly = !hasSignal &&
                               !string.IsNullOrWhiteSpace(emailNorm) &&
                               clientEmails.Contains(emailNorm, StringComparer.OrdinalIgnoreCase);

            // Inclusion rule:
            // - must not be assistant
            // - and (has agent signals OR founder OR internal domain)
            // - and not a client-only identity when no agent signal
            if ((hasSignal || isFounder || isInternalDomain) && !isClientOnly)
            {
                Seed(p.AgentUserId, p.AgentUpn, p.FullName, p.Title, p.UpdatedUtc, p.DisplayOrder);
            }
        }

        // Fall back: ensure internal-domain tracking profiles appear even if an AgentProfile was never created.
        foreach (var tp in trackingProfiles)
        {
            var oid = Norm(tp.AgentUserId);
            if (string.IsNullOrWhiteSpace(oid)) continue;

            var emailNorm = NormEmail(tp.AgentUpn);
            var isAssistant = assistantOids.Any(a => string.Equals(Norm(a), oid, StringComparison.OrdinalIgnoreCase)) ||
                              (!string.IsNullOrWhiteSpace(emailNorm) && assistantEmails.Contains(emailNorm, StringComparer.OrdinalIgnoreCase));
            if (isAssistant) continue;

            var isClientOnly = !string.IsNullOrWhiteSpace(emailNorm) &&
                               clientEmails.Contains(emailNorm, StringComparer.OrdinalIgnoreCase);

            var isInternalDomain = !string.IsNullOrWhiteSpace(internalDomain) &&
                                   !string.IsNullOrWhiteSpace(emailNorm) &&
                                   emailNorm.EndsWith("@" + internalDomain, StringComparison.OrdinalIgnoreCase);

            if (isInternalDomain && !isClientOnly)
            {
                Seed(tp.AgentUserId, tp.AgentUpn, tp.DisplayName, null, tp.UpdatedUtc);
            }
        }

        var agentClientIds = await _db.AgentClients
            .AsNoTracking()
            .Select(x => new { x.AgentUserId, x.AgentUpn })
            .ToListAsync();
        foreach (var ac in agentClientIds)
        {
            // Only backfill existing agents; do not create new ones from client rows.
            var key = Norm(ac.AgentUserId);
            if (map.TryGetValue(key, out var descriptor) && string.IsNullOrWhiteSpace(descriptor.Email))
                descriptor.Email = ac.AgentUpn;
        }

        var leadAgents = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Select(x => x.AgentUserId)
            .ToListAsync();
        foreach (var id in leadAgents)
        {
            // Only enrich existing agents; ignore unknown IDs to avoid stale entries.
            var key = Norm(id);
            if (map.TryGetValue(key, out _))
                continue;
        }

        return map;
    }

    public async Task<AgencyCommandDashboardVm> GetDashboardAsync(ClaimsPrincipal user)
    {
        // SECURITY: founder-only; this service is shared by controller + any future callers.
        // Enforce at the service layer so hiding the nav or removing attributes cannot bypass.
        FounderGuard.EnsureFounderOrThrow(user);

        var descriptors = await DiscoverAgentsAsync();

        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = GetWeekStartUtc(now);

        var leadRows = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Select(l => new
            {
                l.AgentUserId,
                l.Bucket,
                l.CrmStage,
                l.CrmStatus,
                l.CallsToday,
                l.CallsWeek,
                l.CallsTodayDateUtc,
                l.CallsWeekStartUtc
            })
            .ToListAsync();

        var clientRows = await (from ac in _db.AgentClients.AsNoTracking()
                                join cp in _db.ClientProfiles.AsNoTracking() on ac.ClientUserId equals cp.ClientUserId
                                select new
                                {
                                    ac.AgentUserId,
                                    ac.AgentUpn,
                                    cp.ClientUserId,
                                    cp.FirstName,
                                    cp.LastName,
                                    cp.Email,
                                    cp.CrmNotes,
                                    cp.CrmStatus,
                                    cp.CrmPriority,
                                    cp.CrmNextDate,
                                    cp.CrmNextText
                                })
            .ToListAsync();

        var filteredClientRows = clientRows
            .Where(row => IsClientRecordType(ResolveClientRecordType(row.ClientUserId, row.CrmNotes)))
            .ToList();

        // Fill descriptor emails when we only had AgentUpn from AgentClients
        foreach (var row in filteredClientRows)
        {
            if (descriptors.TryGetValue(Norm(row.AgentUserId), out var desc) && string.IsNullOrWhiteSpace(desc.Email))
                desc.Email = row.AgentUpn;
        }

        var leadGroups = leadRows.GroupBy(l => Norm(l.AgentUserId));
        var clientGroups = filteredClientRows.GroupBy(c => Norm(c.AgentUserId));

        var pipelineByAgent = leadGroups.ToDictionary(
            g => g.Key,
            g =>
            {
                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var stage in g.Select(x => NormalizeStage(x.CrmStage, x.Bucket)))
                {
                    var key = string.IsNullOrWhiteSpace(stage) ? "New" : stage.Trim();
                    dict[key] = dict.TryGetValue(key, out var val) ? val + 1 : 1;
                }
                return dict;
            },
            StringComparer.OrdinalIgnoreCase);

        var callsTodayByAgent = leadGroups.ToDictionary(
            g => g.Key,
            g => g.Where(x => x.CallsTodayDateUtc.HasValue && x.CallsTodayDateUtc.Value.Date == today)
                  .Sum(x => x.CallsToday),
            StringComparer.OrdinalIgnoreCase);

        var callsWeekByAgent = leadGroups.ToDictionary(
            g => g.Key,
            g => g.Where(x => x.CallsWeekStartUtc.HasValue && x.CallsWeekStartUtc.Value.Date == weekStart)
                  .Sum(x => x.CallsWeek),
            StringComparer.OrdinalIgnoreCase);

        var followUpsByAgent = clientGroups.ToDictionary(
            g => g.Key,
            g => g.Count(x => x.CrmNextDate.HasValue && x.CrmNextDate.Value <= now),
            StringComparer.OrdinalIgnoreCase);

        var clientCounts = clientGroups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var leadCounts = leadGroups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var cards = new List<AgentCommandCardVm>();

        // Deduplicate by normalized email, prefer the most recently updated profile.
        var byEmail = descriptors.Values
            .Where(d => !string.IsNullOrWhiteSpace(d.Email))
            .GroupBy(d => Norm(d.Email))
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToList();

        foreach (var desc in byEmail)
        {
            var key = Norm(desc.AgentUserId);

            cards.Add(new AgentCommandCardVm
            {
                AgentUserId = key,
                Email = desc.Email,
                FullName = BuildDisplayName(desc.FullName, desc.Email),
                Title = desc.Title,
                ClientCount = clientCounts.TryGetValue(key, out var cc) ? cc : 0,
                LeadCount = leadCounts.TryGetValue(key, out var lc) ? lc : 0,
                CallsToday = callsTodayByAgent.TryGetValue(key, out var ct) ? ct : 0,
                CallsWeek = callsWeekByAgent.TryGetValue(key, out var cw) ? cw : 0,
                FollowUpsDue = followUpsByAgent.TryGetValue(key, out var fu) ? fu : 0,
                PipelineByStage = pipelineByAgent.TryGetValue(key, out var p) ? p : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                DisplayOrder = desc.DisplayOrder
            });
        }

        var overall = new CommandKpiVm
        {
            TotalAgents = cards.Count,
            TotalLeads = cards.Sum(c => c.LeadCount),
            TotalClients = cards.Sum(c => c.ClientCount),
            CallsToday = cards.Sum(c => c.CallsToday),
            CallsWeek = cards.Sum(c => c.CallsWeek),
            FollowUpsDue = cards.Sum(c => c.FollowUpsDue)
        };

        // Revenue intelligence summary for modal
        var localTz = ResolveLocalTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, localTz);
        var (leadsTotals, clientTotals, byAgent) = await _production.GetAgencyTotalsAsync();
        var monthlyData = await _production.GetMonthlyProducerBreakdownAsync(localTz, nowLocal.Year, nowLocal.Month);

        var monthly = Enumerable.Range(1, nowLocal.Month)
            .Select(m => monthlyData.FirstOrDefault(x => x.Month == m) ?? new MonthlyProducerVm { Month = m })
            .ToList();

        var names = _db.AgentProfiles.AsNoTracking()
            .ToDictionary(a => a.AgentUserId, a => string.IsNullOrWhiteSpace(a.FullName) ? a.AgentUpn : a.FullName, StringComparer.OrdinalIgnoreCase);

        var revenueVm = new AgencyRevenueVm
        {
            Leads = leadsTotals,
            Clients = clientTotals,
            ByAgent = byAgent,
            Monthly = monthly,
            AgentNames = names,
            CurrentMonth = nowLocal.Month,
            Year = nowLocal.Year
        };

        return new AgencyCommandDashboardVm
        {
            Agents = cards
                .OrderBy(c => c.DisplayOrder ?? int.MaxValue)
                .ThenByDescending(c => c.LeadCount)
                .ThenByDescending(c => c.ClientCount)
                .ThenBy(c => c.FullName)
                .ToList(),
            Overall = overall,
            Revenue = revenueVm
        };
    }

    public async Task<AgentCommandDetailVm?> GetAgentDetailAsync(ClaimsPrincipal user, string agentUserId)
    {
        // SECURITY: founder-only read path. We never mutate agent data here; only inspect
        // it under the founder identity. Do NOT relax this without a full threat review.
        FounderGuard.EnsureFounderOrThrow(user);

        var agentKey = Norm(agentUserId);
        if (string.IsNullOrWhiteSpace(agentKey)) return null;

        var descriptors = await DiscoverAgentsAsync();
        if (!descriptors.TryGetValue(agentKey, out var desc))
        {
            _logger.LogWarning("AgencyCommand detail requested for unknown agent {Agent}", agentUserId);
            return null;
        }

        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = GetWeekStartUtc(now);

        var leads = await _db.WorkstationLeadProfiles
            .AsNoTracking()
            .Where(l => l.AgentUserId.ToLower() == agentKey)
            .Select(l => new
            {
                l.LeadId,
                l.FirstName,
                l.LastName,
                l.Email,
                l.Phone,
                l.CrmStage,
                l.CrmStatus,
                l.Bucket,
                l.CallsToday,
                l.CallsWeek,
                l.CallsTodayDateUtc,
                l.CallsWeekStartUtc
            })
            .ToListAsync();

        var clients = await (from ac in _db.AgentClients.AsNoTracking()
                             join cp in _db.ClientProfiles.AsNoTracking() on ac.ClientUserId equals cp.ClientUserId
                             where ac.AgentUserId.ToLower() == agentKey
                             select new
                             {
                                 cp.ClientUserId,
                                 cp.FirstName,
                                 cp.LastName,
                                 cp.Email,
                                 cp.CrmNotes,
                                 cp.CrmStatus,
                                 cp.CrmPriority,
                                 cp.CrmNextDate,
                                 cp.CrmNextText
                             })
            .ToListAsync();

        var typedClients = clients
            .Where(c => IsClientRecordType(ResolveClientRecordType(c.ClientUserId, c.CrmNotes)))
            .ToList();

        var pipelineDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in leads.Select(l => NormalizeStage(l.CrmStage, l.Bucket)))
        {
            var key = string.IsNullOrWhiteSpace(stage) ? "New" : stage.Trim();
            pipelineDict[key] = pipelineDict.TryGetValue(key, out var val) ? val + 1 : 1;
        }

        var followUps = typedClients
            .Where(c => c.CrmNextDate.HasValue && c.CrmNextDate.Value <= now)
            .Select(c => new FollowUpItemVm
            {
                ClientUserId = c.ClientUserId,
                Name = BuildDisplayName($"{c.FirstName} {c.LastName}", c.Email),
                DueDate = c.CrmNextDate,
                Note = c.CrmNextText
            })
            .OrderBy(c => c.DueDate ?? DateTime.MaxValue)
            .ToList();

        var card = new AgentCommandCardVm
        {
            AgentUserId = agentKey,
            Email = desc.Email,
            FullName = BuildDisplayName(desc.FullName, desc.Email),
            Title = desc.Title,
            LeadCount = leads.Count,
            ClientCount = typedClients.Count,
            CallsToday = leads.Where(l => l.CallsTodayDateUtc.HasValue && l.CallsTodayDateUtc.Value.Date == today).Sum(l => l.CallsToday),
            CallsWeek = leads.Where(l => l.CallsWeekStartUtc.HasValue && l.CallsWeekStartUtc.Value.Date == weekStart).Sum(l => l.CallsWeek),
            FollowUpsDue = followUps.Count,
            PipelineByStage = pipelineDict
        };

        // TODO: Surface appointments / policies once persisted in the DB; do not fake counts.
        var detail = new AgentCommandDetailVm
        {
            Agent = card,
            Leads = leads
                .Select(l => new LeadSummaryVm
                {
                    LeadId = l.LeadId,
                    Name = BuildDisplayName($"{l.FirstName} {l.LastName}", l.Email),
                    Email = l.Email ?? "",
                    Phone = l.Phone ?? "",
                    Stage = NormalizeStage(l.CrmStage, l.Bucket),
                    Status = string.IsNullOrWhiteSpace(l.CrmStatus) ? "Lead" : l.CrmStatus,
                    CallsToday = l.CallsTodayDateUtc.HasValue && l.CallsTodayDateUtc.Value.Date == today ? l.CallsToday : 0,
                    CallsWeek = l.CallsWeekStartUtc.HasValue && l.CallsWeekStartUtc.Value.Date == weekStart ? l.CallsWeek : 0
                })
                .ToList(),
            Clients = typedClients
                .Select(c => new ClientSummaryVm
                {
                    ClientUserId = c.ClientUserId,
                    Name = BuildDisplayName($"{c.FirstName} {c.LastName}", c.Email),
                    Email = c.Email ?? "",
                    Priority = c.CrmPriority,
                    Status = c.CrmStatus,
                    NextTouch = c.CrmNextDate,
                    NextNote = c.CrmNextText
                })
                .OrderBy(c => c.NextTouch ?? DateTime.MaxValue)
                .ToList(),
            ViewAsEnabled = true, // explicit: only founder can reach this path
            EffectiveAgentName = BuildDisplayName(desc.FullName, desc.Email),
            EffectiveAgentEmail = desc.Email,
            Kpi = new CommandKpiVm
            {
                TotalAgents = 1,
                TotalClients = typedClients.Count,
                TotalLeads = leads.Count,
                CallsToday = card.CallsToday,
                CallsWeek = card.CallsWeek,
                FollowUpsDue = card.FollowUpsDue
            },
            Pipeline = pipelineDict
                .Select(p => new PipelineSliceVm { Stage = p.Key, Count = p.Value })
                .OrderByDescending(p => p.Count)
                .ToList(),
            FollowUps = followUps
        };

        return detail;
    }

    private static DateTime GetWeekStartUtc(DateTime now)
    {
        // Start week on Monday for consistency with most sales dashboards.
        var diff = ((int)now.DayOfWeek + 6) % 7;
        return now.Date.AddDays(-diff);
    }

    private static TimeZoneInfo ResolveLocalTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"); }
        catch { return TimeZoneInfo.Local; }
    }
}
