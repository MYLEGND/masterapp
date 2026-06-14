using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Analytics;

public sealed class MetaAdsService : IMetaAdsService
{
    private readonly string? _envFilter;
    private readonly bool _excludeLocalHosts;
    private readonly IConfiguration _config;
    private readonly MasterAppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMetaAdsConnectionStore _connectionStore;
    private readonly ILogger<MetaAdsService> _logger;

    public MetaAdsService(IConfiguration config, MasterAppDbContext db, IHttpClientFactory httpClientFactory, IMetaAdsConnectionStore connectionStore, ILogger<MetaAdsService> logger)
    {
        _config = config;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _connectionStore = connectionStore;
        _logger = logger;
        _envFilter = NormalizeEnv(config["Analytics:EnvironmentFilter"] ?? config["Analytics__EnvironmentFilter"]);
        _excludeLocalHosts = ParseBool(config["Analytics:ExcludeLocalHosts"] ?? config["Analytics__ExcludeLocalHosts"]);
    }

    public async Task<MetaCampaignsDto> GetCampaignsAsync(TimeRangeRequest range, ScopeContext scope, CancellationToken ct = default)
    {
        var enabled = _config.GetValue<bool?>("MetaAds:Enabled") ?? false;
        if (!enabled)
            throw new InvalidOperationException("Meta Ads integration is disabled. Set MetaAds:Enabled=true.");

        var (token, accountId) = await ResolveCredentialsAsync(scope, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Meta Ads access token missing. Connect Meta Ads or set MetaAds:AccessToken.");
        if (string.IsNullOrWhiteSpace(accountId))
            throw new InvalidOperationException("No Meta Ads account mapping found for this agent. Connect Meta Ads to bind an account.");

        var version = (_config["MetaAds:ApiVersion"] ?? "v21.0").Trim();
        if (string.IsNullOrWhiteSpace(version)) version = "v21.0";

        var client = _httpClientFactory.CreateClient("ResilientDefault");
        var accountMetadata = await FetchAccountMetadataAsync(client, version, accountId, token, range, ct);

        var campaigns = await FetchCampaignDefinitionsAsync(client, version, accountId, token, ct);
        var insights = await FetchCampaignInsightsAsync(client, version, accountId, token, range, accountMetadata.TimeZone, ct);
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope, ct);
        var websiteLeadCounts = await BuildWebsiteLeadCountsAsync(range, scope, scopedAgentIds, campaigns, ct);
        var campaignOutcomes = await BuildCampaignOutcomeCountsAsync(range, scope, scopedAgentIds, campaigns, ct);

        var rows = campaigns
            .Select(c =>
            {
                insights.TryGetValue(c.CampaignId, out var i);
                var websiteLeads = websiteLeadCounts.TryGetValue(c.CampaignId, out var websiteLeadCount)
                    ? websiteLeadCount
                    : 0;
                var metaLeads = i?.Leads ?? 0;
                campaignOutcomes.TryGetValue(c.CampaignId, out var outcomes);
                var paidPremium = outcomes?.PaidPremium ?? 0m;
                var spend = i?.Spend ?? 0m;
                return new MetaCampaignRow
                {
                    CampaignId = c.CampaignId,
                    CampaignName = c.CampaignName,
                    Status = c.Status,
                    Objective = c.Objective,
                    StartTimeUtc = c.StartTimeUtc,
                    StopTimeUtc = c.StopTimeUtc,
                    UpdatedTimeUtc = c.UpdatedTimeUtc,
                    Spend = spend,
                    Impressions = i?.Impressions ?? 0,
                    Reach = i?.Reach ?? 0,
                    Clicks = i?.Clicks ?? 0,
                    Ctr = i?.Ctr ?? 0m,
                    Cpc = i?.Cpc ?? 0m,
                    Cpm = i?.Cpm ?? 0m,
                    Frequency = i?.Frequency ?? 0m,
                    Leads = metaLeads,
                    WebsiteLeads = websiteLeads,
                    WebsiteLeadGap = metaLeads - websiteLeads,
                    QualifiedLeads = outcomes?.QualifiedLeads ?? 0,
                    Appointments = outcomes?.Appointments ?? 0,
                    Applications = outcomes?.Applications ?? 0,
                    PoliciesIssued = outcomes?.PoliciesIssued ?? 0,
                    PoliciesPaid = outcomes?.PoliciesPaid ?? 0,
                    PaidPremium = paidPremium,
                    PremiumRoas = spend > 0m ? Math.Round(paidPremium / spend, 2) : 0m
                };
            })
            .OrderByDescending(x => x.Spend)
            .ThenByDescending(x => x.Impressions)
            .ThenBy(x => x.CampaignName)
            .ToList();

        return new MetaCampaignsDto
        {
            AccountId = accountId,
            AccountName = accountMetadata.AccountName,
            RangeLabel = range.Label,
            TimeZoneLabel = accountMetadata.TimeZoneLabel,
            ComparisonNote = $"Meta Leads are reported by the Meta Ads API. Website Leads are server-confirmed leads captured on your site and attributed by meta_campaign_id, utm_id, then utm_campaign fallback. These may differ because of attribution windows, browser restrictions, CAPI configuration, or reporting delay. Date range is aligned to the Meta account timezone ({accountMetadata.TimeZoneLabel}).",
            SyncedUtc = DateTime.UtcNow,
            Rows = rows
        };
    }

    private async Task<Guid[]?> ResolveScopedAgentIdsAsync(ScopeContext scope, CancellationToken ct)
    {
        if (scope.ScopeType != ScopeType.Agent || !scope.AgentTrackingProfileId.HasValue)
            return null;

        var selectedId = scope.AgentTrackingProfileId.Value;
        var upn = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == selectedId)
            .Select(p => p.AgentUpn)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(upn))
            return new[] { selectedId };

        var ids = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.AgentUpn == upn)
            .Select(p => p.Id)
            .Distinct()
            .ToListAsync(ct);

        if (!ids.Contains(selectedId))
            ids.Add(selectedId);

        return ids.ToArray();
    }

    private async Task<Dictionary<string, long>> BuildWebsiteLeadCountsAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds,
        IReadOnlyCollection<MetaCampaignSeed> campaigns,
        CancellationToken ct)
    {
        if (campaigns.Count == 0)
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var campaignIds = campaigns
            .Select(c => NormalizeCampaignKey(c.CampaignId))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var campaignNameMap = campaigns
            .Select(c => new
            {
                Name = NormalizeCampaignKey(c.CampaignName),
                Id = NormalizeCampaignKey(c.CampaignId)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id!, StringComparer.OrdinalIgnoreCase);

        var leads = await BaseWebsiteLeads(range, scope, scopedAgentIds)
            .Select(l => new WebsiteLeadAttributionSeed
            {
                UtmCampaign = l.UtmCampaign,
                UtmId = l.UtmId,
                MetaCampaignId = l.MetaCampaignId,
                MetadataJson = l.MetadataJson
            })
            .ToListAsync(ct);

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var lead in leads)
        {
            var metadata = ReadLeadMetadata(lead.MetadataJson);
            var metaCampaignId = NormalizeCampaignKey(lead.MetaCampaignId) ?? metadata.MetaCampaignId;
            var utmId = NormalizeCampaignKey(lead.UtmId) ?? metadata.UtmId;
            var utmCampaign = NormalizeCampaignKey(lead.UtmCampaign);

            string? matchedCampaignId = null;

            if (!string.IsNullOrWhiteSpace(metaCampaignId) && campaignIds.Contains(metaCampaignId))
            {
                matchedCampaignId = metaCampaignId;
            }
            else if (!string.IsNullOrWhiteSpace(utmId) && campaignIds.Contains(utmId))
            {
                matchedCampaignId = utmId;
            }
            else if (!string.IsNullOrWhiteSpace(utmCampaign) && campaignNameMap.TryGetValue(utmCampaign, out var byName))
            {
                matchedCampaignId = byName;
            }

            if (string.IsNullOrWhiteSpace(matchedCampaignId))
                continue;

            counts[matchedCampaignId] = counts.TryGetValue(matchedCampaignId, out var current)
                ? current + 1
                : 1;
        }

        return counts;
    }

    private async Task<Dictionary<string, CampaignOutcomeTotals>> BuildCampaignOutcomeCountsAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds,
        IReadOnlyCollection<MetaCampaignSeed> campaigns,
        CancellationToken ct)
    {
        if (campaigns.Count == 0)
            return new Dictionary<string, CampaignOutcomeTotals>(StringComparer.OrdinalIgnoreCase);

        var campaignIds = campaigns
            .Select(c => NormalizeCampaignKey(c.CampaignId))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var campaignNameMap = campaigns
            .Select(c => new
            {
                Name = NormalizeCampaignKey(c.CampaignName),
                Id = NormalizeCampaignKey(c.CampaignId)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id!, StringComparer.OrdinalIgnoreCase);

        var events = await BaseMetaSignalEvents(range, scope, scopedAgentIds)
            .Where(e =>
                e.EventName == "QualifiedLead" ||
                e.EventName == "AppointmentBooked" ||
                e.EventName == "Schedule" ||
                e.EventName == "ApplicationSubmitted" ||
                e.EventName == "SubmitApplication" ||
                e.EventName == "PolicyIssued" ||
                e.EventName == "CompleteRegistration" ||
                e.EventName == "PolicyPaid" ||
                e.EventName == "Purchase")
            .Select(e => new MetaSignalOutcomeSeed
            {
                EventName = e.EventName,
                UtmCampaign = e.UtmCampaign,
                UtmId = e.UtmId,
                MetadataJson = e.MetadataJson
            })
            .ToListAsync(ct);

        var result = new Dictionary<string, CampaignOutcomeTotals>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in events)
        {
            var metaCampaignId = ReadResolvedAttributionString(signal.MetadataJson, "metaCampaignId");
            var utmId = NormalizeCampaignKey(signal.UtmId) ?? ReadResolvedAttributionString(signal.MetadataJson, "utmId");
            var utmCampaign = NormalizeCampaignKey(signal.UtmCampaign) ?? ReadResolvedAttributionString(signal.MetadataJson, "utmCampaign");

            string? matchedCampaignId = null;

            if (!string.IsNullOrWhiteSpace(metaCampaignId) && campaignIds.Contains(metaCampaignId))
                matchedCampaignId = metaCampaignId;
            else if (!string.IsNullOrWhiteSpace(utmId) && campaignIds.Contains(utmId))
                matchedCampaignId = utmId;
            else if (!string.IsNullOrWhiteSpace(utmCampaign) && campaignNameMap.TryGetValue(utmCampaign, out var byName))
                matchedCampaignId = byName;

            if (string.IsNullOrWhiteSpace(matchedCampaignId))
                continue;

            if (!result.TryGetValue(matchedCampaignId, out var totals))
            {
                totals = new CampaignOutcomeTotals();
                result[matchedCampaignId] = totals;
            }

            switch (signal.EventName)
            {
                case "QualifiedLead":
                    totals.QualifiedLeads++;
                    break;
                case "AppointmentBooked":
                case "Schedule":
                    totals.Appointments++;
                    break;
                case "ApplicationSubmitted":
                case "SubmitApplication":
                    totals.Applications++;
                    break;
                case "PolicyIssued":
                case "CompleteRegistration":
                    totals.PoliciesIssued++;
                    break;
                case "PolicyPaid":
                case "Purchase":
                    totals.PoliciesPaid++;
                    totals.PaidPremium += ReadOutcomeValue(signal.MetadataJson);
                    break;
            }
        }

        return result;
    }

    private IQueryable<MetaSignalEvent> BaseMetaSignalEvents(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds) =>
        _db.MetaSignalEvents.AsNoTracking()
            .Where(e => e.CreatedUtc >= range.FromUtc && e.CreatedUtc <= range.ToUtc)
            .Where(e => _envFilter == "prod"
                ? e.Environment == "prod" || e.Environment == "production" || e.Environment == "Prod" || e.Environment == "Production"
                : _envFilter == "dev"
                    ? e.Environment == "dev" || e.Environment == "development" || e.Environment == "Dev" || e.Environment == "Development"
                    : e.Environment == null || e.Environment == "" ||
                      e.Environment == "prod" || e.Environment == "production" || e.Environment == "Prod" || e.Environment == "Production" ||
                      e.Environment == "dev" || e.Environment == "development" || e.Environment == "Dev" || e.Environment == "Development")
            .Where(e => !_excludeLocalHosts ||
                e.Host == null || e.Host == "" ||
                (!e.Host.StartsWith("localhost") &&
                 !e.Host.StartsWith("127.0.0.1") &&
                 !e.Host.StartsWith("::1") &&
                 !e.Host.StartsWith("[::1]")))
            .Where(e => scope.ScopeType != ScopeType.Agent || !scope.AgentTrackingProfileId.HasValue
                ? true
                : scopedAgentIds != null
                    ? e.AgentTrackingProfileId.HasValue && scopedAgentIds.Contains(e.AgentTrackingProfileId.Value)
                    : e.AgentTrackingProfileId == scope.AgentTrackingProfileId.Value);

    private IQueryable<WebsiteLead> BaseWebsiteLeads(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal)
            .Where(l => !l.IsDeleted)
            .Where(l => l.CreatedUtc >= range.FromUtc && l.CreatedUtc <= range.ToUtc)
            .Where(LeadEnvironmentPredicate())
            .Where(LeadHostPredicate())
            .Where(LeadScopePredicate(scope, scopedAgentIds));

    private System.Linq.Expressions.Expression<Func<WebsiteLead, bool>> LeadEnvironmentPredicate()
    {
        if (_envFilter == "prod")
            return l => l.Environment == "prod" || l.Environment == "production" || l.Environment == "Prod" || l.Environment == "Production";
        if (_envFilter == "dev")
            return l => l.Environment == "dev" || l.Environment == "development" || l.Environment == "Dev" || l.Environment == "Development";

        return l =>
            l.Environment == null || l.Environment == "" ||
            l.Environment == "prod" || l.Environment == "production" || l.Environment == "Prod" || l.Environment == "Production" ||
            l.Environment == "dev" || l.Environment == "development" || l.Environment == "Dev" || l.Environment == "Development";
    }

    private System.Linq.Expressions.Expression<Func<WebsiteLead, bool>> LeadHostPredicate()
    {
        if (!_excludeLocalHosts)
            return l => true;

        return l =>
            l.Host == null || l.Host == "" ||
            (!l.Host.StartsWith("localhost") &&
             !l.Host.StartsWith("127.0.0.1") &&
             !l.Host.StartsWith("::1") &&
             !l.Host.StartsWith("[::1]"));
    }

    private static System.Linq.Expressions.Expression<Func<WebsiteLead, bool>> LeadScopePredicate(ScopeContext scope, Guid[]? scopedAgentIds)
    {
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds != null && scopedAgentIds.Length > 0)
            {
                return l => l.AgentTrackingProfileId.HasValue && scopedAgentIds.Contains(l.AgentTrackingProfileId.Value);
            }

            var agentId = scope.AgentTrackingProfileId.Value;
            return l => l.AgentTrackingProfileId == agentId;
        }

        return l => true;
    }

    private static bool ParseBool(string? value) =>
        !string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var parsed) && parsed;

    private static string? NormalizeEnv(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return null;
        var value = env.Trim().ToLowerInvariant();
        if (value.StartsWith("prod")) return "prod";
        if (value.StartsWith("dev")) return "dev";
        return null;
    }

    private static string? NormalizeCampaignKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadResolvedAttributionString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("resolvedAttribution", out var resolvedAttribution) ||
                resolvedAttribution.ValueKind != JsonValueKind.Object ||
                !resolvedAttribution.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return NormalizeCampaignKey(property.GetString());
        }
        catch
        {
            return null;
        }
    }

    private static decimal ReadOutcomeValue(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return 0m;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            foreach (var propertyName in new[] { "personalAmount", "PersonalAmount", "value", "Value", "amount", "Amount" })
            {
                if (!root.TryGetProperty(propertyName, out var property))
                    continue;

                if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var numeric))
                    return numeric;

                if (property.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        }
        catch
        {
            return 0m;
        }

        return 0m;
    }

    private static WebsiteLeadMetadataSeed ReadLeadMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new WebsiteLeadMetadataSeed();

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            string? ReadString(string propertyName) =>
                root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                    ? NormalizeCampaignKey(value.GetString())
                    : null;

            return new WebsiteLeadMetadataSeed
            {
                UtmId = ReadString("UtmId"),
                MetaCampaignId = ReadString("MetaCampaignId")
            };
        }
        catch
        {
            return new WebsiteLeadMetadataSeed();
        }
    }

    private async Task<(string Token, string AccountId)> ResolveCredentialsAsync(ScopeContext scope, CancellationToken ct)
    {
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue && scope.AgentTrackingProfileId.Value != Guid.Empty)
        {
            var connection = await _connectionStore.GetAsync(scope.AgentTrackingProfileId.Value, ct);
            if (connection != null && !string.IsNullOrWhiteSpace(connection.AccessToken))
            {
                var account = NormalizeAccountId(connection.AccountId);
                return (connection.AccessToken.Trim(), account ?? string.Empty);
            }
        }

        var token = (_config["MetaAds:AccessToken"] ?? string.Empty).Trim();
        var accountId = await ResolveAccountIdAsync(scope, ct);
        return (token, accountId);
    }

    private async Task<string> ResolveAccountIdAsync(ScopeContext scope, CancellationToken ct)
    {
        var map = _config.GetSection("MetaAds:AgentAccountMap").Get<Dictionary<string, string>>()
                  ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? defaultAccount = NormalizeAccountId(_config["MetaAds:DefaultAccountId"]);

        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            var profileId = scope.AgentTrackingProfileId.Value;
            var profile = await _db.AgentTrackingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == profileId, ct);

            var keys = new List<string>
            {
                profileId.ToString(),
                profileId.ToString("D"),
                profileId.ToString("N")
            };

            if (!string.IsNullOrWhiteSpace(profile?.Slug)) keys.Add(profile.Slug.Trim());
            if (!string.IsNullOrWhiteSpace(profile?.AgentUpn)) keys.Add(profile.AgentUpn.Trim());
            if (!string.IsNullOrWhiteSpace(profile?.AgentUserId)) keys.Add(profile.AgentUserId.Trim());

            foreach (var key in keys)
            {
                if (TryGetMappedAccount(map, key, out var mapped)) return mapped;
                if (TryGetMappedAccount(map, key.ToLowerInvariant(), out mapped)) return mapped;
            }

            return defaultAccount ?? string.Empty;
        }

        return defaultAccount ?? string.Empty;
    }

    private static bool TryGetMappedAccount(IReadOnlyDictionary<string, string> map, string key, out string accountId)
    {
        accountId = string.Empty;
        if (!map.TryGetValue(key, out var raw)) return false;
        var normalized = NormalizeAccountId(raw);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        accountId = normalized;
        return true;
    }

    private static string? NormalizeAccountId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("act_", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(4);
        return trimmed;
    }

    private static DateTime? ParseDateTimeUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    private async Task<List<MetaCampaignSeed>> FetchCampaignDefinitionsAsync(HttpClient client, string version, string accountId, string token, CancellationToken ct)
    {
        var rows = new List<MetaCampaignSeed>();
        string? nextUrl = BuildCampaignsUrl(version, accountId, token);

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta campaigns fetch failed. status={Status} body={Body}", (int)res.StatusCode, TrimForLog(json));
                throw new InvalidOperationException("Unable to load campaigns from Meta Ads API.");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Meta Ads API error: {err.GetProperty("message").GetString()}");

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var campaignId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(campaignId)) continue;

                    var status = item.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
                    var effective = item.TryGetProperty("effective_status", out var esEl) ? esEl.GetString() ?? "" : "";
                    var configured = item.TryGetProperty("configured_status", out var csEl) ? csEl.GetString() ?? "" : "";

                    rows.Add(new MetaCampaignSeed
                    {
                        CampaignId = campaignId,
                        CampaignName = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                        Objective = item.TryGetProperty("objective", out var objEl) ? objEl.GetString() ?? "" : "",
                        Status = !string.IsNullOrWhiteSpace(effective) ? effective : (!string.IsNullOrWhiteSpace(status) ? status : configured),
                        StartTimeUtc = ParseDateTimeUtc(item.TryGetProperty("start_time", out var stEl) ? stEl.GetString() : null),
                        StopTimeUtc = ParseDateTimeUtc(item.TryGetProperty("stop_time", out var spEl) ? spEl.GetString() : null),
                        UpdatedTimeUtc = ParseDateTimeUtc(item.TryGetProperty("updated_time", out var utEl) ? utEl.GetString() : null)
                    });
                }
            }

            nextUrl = null;
            if (root.TryGetProperty("paging", out var paging)
                && paging.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String)
            {
                nextUrl = next.GetString();
            }
        }

        return rows;
    }

    private async Task<Dictionary<string, MetaCampaignInsight>> FetchCampaignInsightsAsync(HttpClient client, string version, string accountId, string token, TimeRangeRequest range, TimeZoneInfo reportTimeZone, CancellationToken ct)
    {
        var map = new Dictionary<string, MetaCampaignInsight>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = BuildInsightsUrl(version, accountId, token, range, reportTimeZone);

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta insights fetch failed. status={Status} body={Body}", (int)res.StatusCode, TrimForLog(json));
                throw new InvalidOperationException("Unable to load campaign insights from Meta Ads API.");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Meta Ads API error: {err.GetProperty("message").GetString()}");

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var campaignId = item.TryGetProperty("campaign_id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(campaignId)) continue;

                    map[campaignId] = new MetaCampaignInsight
                    {
                        Spend = ParseDecimal(item, "spend"),
                        Impressions = ParseLong(item, "impressions"),
                        Reach = ParseLong(item, "reach"),
                        Clicks = ParsePreferredClicks(item),
                        Ctr = ParseDecimal(item, "ctr"),
                        Cpc = ParseDecimal(item, "cpc"),
                        Cpm = ParseDecimal(item, "cpm"),
                        Frequency = ParseDecimal(item, "frequency"),
                        Leads = ParseLeadActions(item)
                    };
                }
            }

            nextUrl = null;
            if (root.TryGetProperty("paging", out var paging)
                && paging.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String)
            {
                nextUrl = next.GetString();
            }
        }

        return map;
    }

    private static string BuildCampaignsUrl(string version, string accountId, string token)
    {
        var fields = "id,name,status,effective_status,configured_status,objective,start_time,stop_time,updated_time";
        return $"https://graph.facebook.com/{version}/act_{accountId}/campaigns?fields={Uri.EscapeDataString(fields)}&limit=500&access_token={Uri.EscapeDataString(token)}";
    }

    private static string BuildInsightsUrl(string version, string accountId, string token, TimeRangeRequest range, TimeZoneInfo reportTimeZone)
    {
        var fields = "campaign_id,campaign_name,spend,impressions,reach,clicks,ctr,cpc,cpm,frequency,actions";
        var since = TimeZoneInfo.ConvertTimeFromUtc(range.FromUtc, reportTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var until = TimeZoneInfo.ConvertTimeFromUtc(range.ToUtc, reportTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var timeRange = $"{{\"since\":\"{since}\",\"until\":\"{until}\"}}";
        return $"https://graph.facebook.com/{version}/act_{accountId}/insights?level=campaign&fields={Uri.EscapeDataString(fields)}&time_range={Uri.EscapeDataString(timeRange)}&limit=500&access_token={Uri.EscapeDataString(token)}";
    }

    private async Task<MetaAccountMetadata> FetchAccountMetadataAsync(HttpClient client, string version, string accountId, string token, TimeRangeRequest range, CancellationToken ct)
    {
        var fallbackTimeZone = range.ViewerTimeZone ?? TimeZoneInfo.Utc;
        var metadata = new MetaAccountMetadata
        {
            AccountName = null,
            TimeZone = fallbackTimeZone,
            TimeZoneLabel = fallbackTimeZone.Id
        };

        try
        {
            var fields = "name,timezone_name,timezone_offset_hours_utc";
            var url = $"https://graph.facebook.com/{version}/act_{accountId}?fields={Uri.EscapeDataString(fields)}&access_token={Uri.EscapeDataString(token)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta account metadata fetch failed. status={Status} body={Body}", (int)res.StatusCode, TrimForLog(json));
                return metadata;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            metadata.AccountName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            if (root.TryGetProperty("timezone_name", out var tzNameEl))
            {
                var tzName = tzNameEl.GetString();
                if (!string.IsNullOrWhiteSpace(tzName))
                {
                    try
                    {
                        metadata.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(tzName.Trim());
                        metadata.TimeZoneLabel = metadata.TimeZone.Id;
                        return metadata;
                    }
                    catch (TimeZoneNotFoundException) { }
                    catch (InvalidTimeZoneException) { }
                }
            }

            if (root.TryGetProperty("timezone_offset_hours_utc", out var offsetEl))
            {
                double? offsetHours = offsetEl.ValueKind switch
                {
                    JsonValueKind.Number when offsetEl.TryGetDouble(out var num) => num,
                    JsonValueKind.String when double.TryParse(offsetEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var strNum) => strNum,
                    _ => null
                };

                if (offsetHours.HasValue)
                {
                    var offset = TimeSpan.FromHours(offsetHours.Value);
                    var direction = offset < TimeSpan.Zero ? "-" : "+";
                    var absOffset = offset.Duration();
                    var label = $"Meta Account UTC{direction}{absOffset.Hours:00}:{absOffset.Minutes:00}";
                    metadata.TimeZone = TimeZoneInfo.CreateCustomTimeZone(
                        $"meta-account-{accountId}",
                        offset,
                        label,
                        label);
                    metadata.TimeZoneLabel = label;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meta account metadata lookup failed for account {AccountId}.", accountId);
        }

        return metadata;
    }

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        return text.Length <= 600 ? text : text.Substring(0, 600);
    }

    private static decimal ParseDecimal(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return 0m;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var dNum)) return dNum;
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dStr)) return dStr;
        return 0m;
    }

    private static long ParseLong(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return 0L;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var nNum)) return nNum;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var nStr)) return nStr;
        return 0L;
    }

    private static long ParsePreferredClicks(JsonElement obj)
    {
        var landingPageViews = ParseActionCount(obj, "landing_page_view");
        if (landingPageViews > 0) return landingPageViews;

        var linkClicks = ParseActionCount(obj, "link_click", "omni_link_click", "outbound_click", "inline_link_click");
        if (linkClicks > 0) return linkClicks;

        return ParseLong(obj, "clicks");
    }

    private static long ParseLeadActions(JsonElement obj)
    {
        return ParseActionCount(
            obj,
            "lead",
            "omni_lead",
            "lead_grouped",
            "onsite_conversion.lead_grouped",
            "offsite_conversion.fb_pixel_lead",
            "onsite_conversion.lead");
    }

    private static long ParseActionCount(JsonElement obj, params string[] actionTypes)
    {
        if (!obj.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return 0L;
        if (actionTypes == null || actionTypes.Length == 0) return 0L;

        long total = 0;
        foreach (var action in actions.EnumerateArray())
        {
            var actionType = action.TryGetProperty("action_type", out var atEl) ? (atEl.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(actionType)) continue;
            if (!actionTypes.Any(t => string.Equals(t, actionType, StringComparison.OrdinalIgnoreCase))) continue;
            var value = action.TryGetProperty("value", out var vEl) ? vEl.GetString() : null;
            if (long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var count))
                total += count;
        }
        return total;
    }

    private sealed class MetaCampaignSeed
    {
        public string CampaignId { get; set; } = "";
        public string CampaignName { get; set; } = "";
        public string Status { get; set; } = "";
        public string Objective { get; set; } = "";
        public DateTime? StartTimeUtc { get; set; }
        public DateTime? StopTimeUtc { get; set; }
        public DateTime? UpdatedTimeUtc { get; set; }
    }

    private sealed class MetaCampaignInsight
    {
        public decimal Spend { get; set; }
        public long Impressions { get; set; }
        public long Reach { get; set; }
        public long Clicks { get; set; }
        public decimal Ctr { get; set; }
        public decimal Cpc { get; set; }
        public decimal Cpm { get; set; }
        public decimal Frequency { get; set; }
        public long Leads { get; set; }
    }

    private sealed class WebsiteLeadAttributionSeed
    {
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetadataJson { get; set; }
    }

    private sealed class WebsiteLeadMetadataSeed
    {
        public string? UtmId { get; set; }
        public string? MetaCampaignId { get; set; }
    }

    private sealed class MetaAccountMetadata
    {
        public string? AccountName { get; set; }
        public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
        public string TimeZoneLabel { get; set; } = "UTC";
    }
    private sealed class MetaSignalOutcomeSeed
    {
        public string EventName { get; set; } = "";
        public string? UtmCampaign { get; set; }
        public string? UtmId { get; set; }
        public string? MetadataJson { get; set; }
    }

    private sealed class CampaignOutcomeTotals
    {
        public long QualifiedLeads { get; set; }
        public long Appointments { get; set; }
        public long Applications { get; set; }
        public long PoliciesIssued { get; set; }
        public long PoliciesPaid { get; set; }
        public decimal PaidPremium { get; set; }
    }

}
