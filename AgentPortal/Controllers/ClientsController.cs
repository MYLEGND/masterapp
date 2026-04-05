using AgentPortal.Models;
using AgentPortal.Helpers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualBasic.FileIO;
using Shared.Auth;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace AgentPortal.Controllers;

[Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public class ClientsController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly ClientProvisioningService _provisioning;
        private readonly IConfiguration _config;
        private readonly ILogger<ClientsController> _logger;
        private readonly IAgentTimeZoneResolver _agentTimeZoneResolver;
        private readonly ProductionService _production;
        private readonly EffectiveAgentContext _agentContext;
        private readonly IExecutionEngine _execution;
        private readonly ICommitmentService _commitments;
        private const string AdvancedMarketsToolId = "AdvancedMarketsInputs";
        private static readonly JsonSerializerOptions AdvancedMarketsStateJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        public ClientsController(
            MasterAppDbContext db,
            ClientProvisioningService provisioning,
            IConfiguration config,
            ILogger<ClientsController> logger,
            IAgentTimeZoneResolver agentTimeZoneResolver,
            ProductionService production,
            EffectiveAgentContext agentContext,
            IExecutionEngine execution,
            ICommitmentService commitments)
        {
            _db = db;
            _provisioning = provisioning;
            _config = config;
            _logger = logger;
            _agentTimeZoneResolver = agentTimeZoneResolver;
            _production = production;
            _agentContext = agentContext;
            _execution = execution;
            _commitments = commitments;
        }

        private static string NormLower(string? v) => (v ?? "").Trim().ToLowerInvariant();
        private static string Norm(string? v) => (v ?? "").Trim();
        private static string? NormalizeEmail(string? email)
        {
            var v = (email ?? "").Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

    private static string NormalizePhoneKey(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("1"))
            digits = digits[1..];
        return digits;
    }

        private static string FormatPhoneDisplay(string? phone)
        {
            var digits = NormalizePhoneKey(phone);
            if (digits.Length != 10) return phone?.Trim() ?? "";
            return $"({digits[..3]}) {digits.Substring(3, 3)}-{digits[6..]}";
        }

    public record CreateClientActionRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public ActionPriority Priority { get; set; } = ActionPriority.P2;
        public bool ShowInCommandCenter { get; set; }
        // Backward compatibility for stale cached clients posting older field names.
        public bool ShowInDashboard { get; set; }
        public bool IncludeInDashboard { get; set; }
    }

    public record CreateClientCommitmentRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public string PromiseText { get; set; } = string.Empty;
        public DateTimeOffset? DueDateUtc { get; set; }
    }

    private static string CommitmentsUnavailableMessage => "Commitments are not live yet in this environment. Apply the latest migrations to enable them.";

    private static string NormalizeTags(string? rawTags)
    {
        rawTags ??= "";
        var parts = rawTags
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", parts);
    }

    private static bool IsBusinessClientRecordType(string? recordType)
        => string.Equals(NormalizeRecordType(recordType), "BusinessClient", StringComparison.OrdinalIgnoreCase);

    private static int? ParseNullableInt(string? raw)
        => int.TryParse(Norm(raw), out var value) ? value : null;

    private static int? DeriveOwnerAge(ClientProfile profile, ClientCrmMeta meta)
    {
        if (ParseNullableInt(meta.Age) is int explicitAge && explicitAge > 0)
            return explicitAge;

        if (!profile.DOB.HasValue)
            return null;

        var today = DateTime.UtcNow.Date;
        var age = today.Year - profile.DOB.Value.Year;
        if (profile.DOB.Value.Date > today.AddYears(-age))
            age--;

        return age > 0 ? age : null;
    }

    private static AdvancedMarketsPageViewModel BuildDefaultAdvancedMarketsInputs(ClientProfile profile, ClientCrmMeta meta)
    {
        var clientName = $"{Norm(profile.FirstName)} {Norm(profile.LastName)}".Trim();

        return new AdvancedMarketsPageViewModel
        {
            Client = new ClientProfileVm
            {
                ClientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName,
                OwnerAge = DeriveOwnerAge(profile, meta),
                State = string.IsNullOrWhiteSpace(meta.State) ? null : meta.State.Trim()
            }
        };
    }

    private static AdvancedMarketsPageViewModel NormalizeAdvancedMarketsInputs(
        AdvancedMarketsPageViewModel? source,
        AdvancedMarketsPageViewModel? fallback = null)
    {
        var model = source ?? fallback ?? new AdvancedMarketsPageViewModel();
        var client = model.Client ?? new ClientProfileVm();
        var fallbackClient = fallback?.Client;

        return new AdvancedMarketsPageViewModel
        {
            Strategy = new StrategySelectionVm
            {
                Selected = model.Strategy?.Selected ?? StrategyKind.DefinedBenefit,
                Sensitivity = string.IsNullOrWhiteSpace(model.Strategy?.Sensitivity) ? "Base" : model.Strategy.Sensitivity.Trim()
            },
            Client = new ClientProfileVm
            {
                ClientName = string.IsNullOrWhiteSpace(client.ClientName) ? fallbackClient?.ClientName : client.ClientName,
                HouseholdName = client.HouseholdName,
                OwnerAge = client.OwnerAge ?? fallbackClient?.OwnerAge,
                SpouseAge = client.SpouseAge,
                RetirementAge = client.RetirementAge,
                State = string.IsNullOrWhiteSpace(client.State) ? fallbackClient?.State : client.State,
                BusinessType = client.BusinessType,
                IncomeType = client.IncomeType,
                Objectives = client.Objectives ?? new List<string>(),
                CurrentQualifiedAssets = client.CurrentQualifiedAssets,
                CurrentTaxableAssets = client.CurrentTaxableAssets,
                CurrentTaxFreeAssets = client.CurrentTaxFreeAssets
            },
            Business = model.Business ?? new BusinessProfileVm(),
            Tax = new TaxAssumptionsVm
            {
                FederalRate = model.Tax?.FederalRate,
                StateRate = model.Tax?.StateRate,
                CapitalGainsRate = model.Tax?.CapitalGainsRate,
                FutureTaxRate = model.Tax?.FutureTaxRate,
                Mode = string.IsNullOrWhiteSpace(model.Tax?.Mode) ? "Simplified" : model.Tax.Mode.Trim()
            },
            Projection = model.Projection ?? new ProjectionInputsVm(),
            DefinedBenefit = model.DefinedBenefit ?? new DefinedBenefitInputsVm(),
            CashBalance = model.CashBalance ?? new CashBalanceInputsVm(),
            Combo = model.Combo ?? new ComboPlanInputsVm(),
            ExecutiveBonus = model.ExecutiveBonus ?? new ExecutiveBonusInputsVm(),
            DeferredComp = model.DeferredComp ?? new DeferredCompInputsVm(),
            SplitDollar = model.SplitDollar ?? new SplitDollarInputsVm()
        };
    }

    private static AdvancedMarketsPageViewModel? DeserializeAdvancedMarketsInputs(string? jsonState)
    {
        if (string.IsNullOrWhiteSpace(jsonState))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<AdvancedMarketsPageViewModel>(jsonState, AdvancedMarketsStateJsonOptions);
            return NormalizeAdvancedMarketsInputs(parsed);
        }
        catch
        {
            return null;
        }
    }

    private static string FingerprintPayload(string json)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    // Financial plan sanitation + merge ----------------------------------------------------------
    private static readonly HashSet<string> DeprecatedRootPlanKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "retirementBase", "retirement_base", "retireBase",
        "investmentStartingBalance", "investmentsStartingBalance", "invStartingBalance",
        "lifeStartingBalance", "annuityStartingBalance",
        "strategy", "scenario", "gapSource", "priorities", "priorityOrder"
    };

    private static readonly HashSet<string> DerivedDistributionInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "wfd_base", "wfd_incomeGap", "wfd_yrsInDist",
        "wfd_invAmt", "wfd_liAmt", "wfd_annAmt"
    };

    private static readonly HashSet<string> DeprecatedDistributionSelects = new(StringComparer.OrdinalIgnoreCase)
    {
        "wfd_strategy", "wfd_pri1", "wfd_pri2", "wfd_pri3", "wfd_pri4",
        "wfd_gapSource", "wfd_scenarioMode"
    };

    private static JsonObject ParsePlanNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return node ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonObject GetOrCreateObj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject obj) return obj;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonObject SanitizeFinancialPlanNode(JsonObject plan)
    {
        // work on a clone so we never mutate callers
        var root = (JsonObject?)plan?.DeepClone() ?? new JsonObject();

        // remove deprecated/derived root members
        foreach (var key in DeprecatedRootPlanKeys)
            root.Remove(key);

        // Back-compat: migrate alternative distribution keys to canonical "distribution"
        if (!root.ContainsKey("distribution"))
        {
            foreach (var altKey in new[] { "distributionPlanner", "distributionPlan", "wealthDistribution", "wfd" })
            {
                if (root[altKey] is JsonObject altObj)
                {
                    root["distribution"] = altObj;
                    break;
                }
            }
        }

        var wf = GetOrCreateObj(root, "wealthForecast");
        var wfInputs = GetOrCreateObj(wf, "inputs");
        // Wealth Forecast outputs should not be persisted as truth
        wf.Remove("results");
        wf.Remove("outputs");

        var dist = GetOrCreateObj(root, "distribution");
        var distInputs = GetOrCreateObj(dist, "inputs");
        var distChecks = GetOrCreateObj(dist, "checks");
        var distSelects = GetOrCreateObj(dist, "selects");
        var distMeta = GetOrCreateObj(dist, "meta");
        var source = distMeta["source"]?.GetValue<string>();
        var hasFinanceSignals =
            distSelects.ContainsKey("wfd_strategy") ||
            distSelects.ContainsKey("wfd_pri1") ||
            distSelects.ContainsKey("wfd_pri2") ||
            distSelects.ContainsKey("wfd_pri3") ||
            distSelects.ContainsKey("wfd_pri4");
        var fromFinance = string.Equals(source, "finance", StringComparison.OrdinalIgnoreCase) || hasFinanceSignals;
        bool manualOverride = false;
        if (distChecks.TryGetPropertyValue("wfd_manualOverride", out var moNode))
        {
            if (moNode is JsonValue jv && jv.TryGetValue<bool>(out var mv)) manualOverride = mv;
            else if (bool.TryParse(moNode?.ToString(), out var parsed)) manualOverride = parsed;
        }

        // strip derived distribution inputs; allow manual override base from finance-only payloads
        foreach (var key in DerivedDistributionInputs)
        {
            if (key.Equals("wfd_base", StringComparison.OrdinalIgnoreCase) && fromFinance && manualOverride)
                continue;
            distInputs.Remove(key);
        }

        // if CRM/unknown payload, do not accept legacy strategy/priorities
        if (!fromFinance)
        {
            foreach (var key in DeprecatedDistributionSelects)
                distSelects.Remove(key);
        }

        return root;
    }

    private static void MergeObject(JsonObject target, JsonObject incoming)
    {
        foreach (var kvp in incoming)
        {
            target[kvp.Key] = kvp.Value?.DeepClone();
        }
    }

    private static string SanitizeFinancialPlanJson(string json, string? existingJson = null)
    {
        var incoming = SanitizeFinancialPlanNode(ParsePlanNode(json));
        var baseline = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : SanitizeFinancialPlanNode(ParsePlanNode(existingJson));

        // merge section-by-section so CRM saves cannot wipe finance-only fields (and vice versa)
        var result = (JsonObject?)baseline.DeepClone() ?? new JsonObject();

        // Wealth Forecast inputs (authoritative from latest payload)
        var incomingWf = GetOrCreateObj(incoming, "wealthForecast");
        var incomingWfInputs = GetOrCreateObj(incomingWf, "inputs");
        if (incomingWfInputs.Count > 0)
        {
            var resWf = GetOrCreateObj(result, "wealthForecast");
            var resWfInputs = GetOrCreateObj(resWf, "inputs");
            MergeObject(resWfInputs, incomingWfInputs);
        }

        // Distribution: merge dictionaries so missing keys keep prior values
        var incomingDist = GetOrCreateObj(incoming, "distribution");
        var resDist = GetOrCreateObj(result, "distribution");

        var incomingInputs = GetOrCreateObj(incomingDist, "inputs");
        if (incomingInputs.Count > 0)
        {
            var resInputs = GetOrCreateObj(resDist, "inputs");
            MergeObject(resInputs, incomingInputs);
        }

        var incomingChecks = GetOrCreateObj(incomingDist, "checks");
        if (incomingChecks.Count > 0)
        {
            var resChecks = GetOrCreateObj(resDist, "checks");
            MergeObject(resChecks, incomingChecks);
        }

        var incomingSelects = GetOrCreateObj(incomingDist, "selects");
        if (incomingSelects.Count > 0)
        {
            var resSelects = GetOrCreateObj(resDist, "selects");
            MergeObject(resSelects, incomingSelects);
        }

        // Distribution canonical input (CRM quick-view payload)
        var incomingCanonical = incomingDist["canonicalInput"] as JsonObject;
        if (incomingCanonical != null && incomingCanonical.Count > 0)
        {
            var resCanonical = resDist["canonicalInput"] as JsonObject;
            if (resCanonical == null)
            {
                resCanonical = new JsonObject();
                resDist["canonicalInput"] = resCanonical;
            }
            MergeObject(resCanonical, incomingCanonical);
        }

        var incomingMeta = incomingDist["meta"] as JsonObject;
        if (incomingMeta != null && incomingMeta.Count > 0)
        {
            var resMeta = GetOrCreateObj(resDist, "meta");
            MergeObject(resMeta, incomingMeta);
        }

        // Return compact JSON
        return result.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static IReadOnlyList<string> GetAdvancedMarketsInvalidFields(ModelStateDictionary modelState)
    {
        var fields = new List<string>();

        foreach (var entry in modelState)
        {
            foreach (var error in entry.Value.Errors)
            {
                var raw = !string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? error.ErrorMessage
                    : error.Exception?.Message;

                var path = ExtractAdvancedMarketsFieldPath(entry.Key, raw);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                fields.Add(DescribeAdvancedMarketsField(path));
            }
        }

        return fields
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ExtractAdvancedMarketsFieldPath(string? modelStateKey, string? errorMessage)
    {
        var direct = NormalizeAdvancedMarketsFieldPath(modelStateKey);
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (string.IsNullOrWhiteSpace(errorMessage))
            return null;

        var match = Regex.Match(errorMessage, @"Path:\s*\$\.((?:inputs\.)?[A-Za-z0-9_.]+)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeAdvancedMarketsFieldPath(match.Groups[1].Value) : null;
    }

    private static string? NormalizeAdvancedMarketsFieldPath(string? raw)
    {
        var path = Norm(raw)
            .TrimStart('$')
            .TrimStart('.');

        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var prefix in new[] { "request.", "Request.", "inputs.", "Inputs." })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        return path.Contains('.') ? path : null;
    }

    private static string DescribeAdvancedMarketsField(string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "Advanced Markets inputs";

        var section = parts[0] switch
        {
            "Client" => "Client Snapshot",
            "Business" => "Business Snapshot",
            "Tax" => "Tax Assumptions",
            "Projection" => "Projection Assumptions",
            "DefinedBenefit" => "Defined Benefit Inputs",
            "CashBalance" => "Cash Balance Inputs",
            "Combo" => "Combo DB + 401(k) / Profit Sharing",
            "ExecutiveBonus" => "Executive Bonus Inputs",
            "DeferredComp" => "Deferred Compensation Inputs",
            "SplitDollar" => "Split-Dollar Inputs",
            "Strategy" => "Strategy",
            _ => HumanizeAdvancedMarketsToken(parts[0])
        };

        var field = HumanizeAdvancedMarketsToken(parts[^1]);
        return $"{section} -> {field}";
    }

    private static string HumanizeAdvancedMarketsToken(string token) => token switch
    {
        "ClientName" => "Client name",
        "HouseholdName" => "Household",
        "OwnerAge" => "Owner age",
        "SpouseAge" => "Spouse age",
        "RetirementAge" => "Retirement age",
        "BusinessType" => "Business type",
        "IncomeType" => "Income type",
        "EntityType" => "Entity type",
        "AnnualBusinessIncome" => "Annual business income",
        "OwnerComp" => "Owner compensation",
        "EmployeeCount" => "Employees (total)",
        "EligibleEmployeeCount" => "Eligible employees",
        "AverageEmployeeAge" => "Average employee age",
        "AverageEmployeeComp" => "Average employee comp",
        "OwnershipPct" => "Owner ownership %",
        "CurrentPlanType" => "Current plan type",
        "CurrentEmployerRetirementContributions" => "Current employer retirement contributions",
        "CurrentBenefitCosts" => "Current benefit costs",
        "FederalRate" => "Federal marginal rate",
        "StateRate" => "State marginal rate",
        "CapitalGainsRate" => "Capital gains rate",
        "FutureTaxRate" => "Future retirement tax rate",
        "Mode" => "Tax mode",
        "CurrentAssets" => "Current investable retirement assets",
        "AnnualSavings" => "Annual savings outside strategy",
        "GrowthRate" => "Growth rate",
        "InflationRate" => "Inflation rate",
        "RetirementDurationYears" => "Retirement duration (years)",
        "DistributionRate" => "Distribution rate",
        "DiscountRate" => "Discount rate",
        "TargetContribution" => "DB target contribution",
        "TargetBenefit" => "DB target benefit",
        "AdminCost" => "Admin cost",
        "EmployeeCostFactor" => "Employee cost factor",
        "IncludeSpouse" => "Include spouse?",
        "SpouseContribution" => "Spouse contribution",
        "Current401kDeferral" => "Current 401(k) deferral",
        "EmployerProfitSharing" => "Employer profit sharing",
        "DesiredTotalContribution" => "Desired total contribution",
        "PayCreditPct" => "Pay credit %",
        "InterestCreditPct" => "Interest credit %",
        "EmployeeDeferral" => "Employee deferral",
        "CatchUp" => "Catch-up",
        "EmployerPct" => "Employer %",
        "ProfitSharingPct" => "Profit sharing %",
        "SafeHarborPct" => "Safe harbor %",
        "TargetTotal" => "Target total contribution",
        "TestingBufferPct" => "Testing buffer %",
        "AnnualBonus" => "Annual bonus",
        "YearsFunded" => "Years funded",
        "PolicyGrowthRate" => "Policy growth rate",
        "DeathBenefitMultiple" => "Death benefit multiple",
        "DeferralAmount" => "Deferral amount",
        "DeferralYears" => "Deferral years",
        "DistributionStartAge" => "Distribution start age",
        "DistributionYears" => "Distribution years",
        "CurrentTaxRate" => "Current tax rate",
        "AnnualPremium" => "Annual premium",
        "FundingYears" => "Funding years",
        "DeathBenefit" => "Death benefit",
        "ExitYear" => "Exit year",
        "CurrentQualifiedAssets" => "Qualified assets (current)",
        "CurrentTaxableAssets" => "Taxable assets (current)",
        "CurrentTaxFreeAssets" => "Tax-free assets (current)",
        "Selected" => "Illustration type",
        _ => Regex.Replace(token, "([a-z0-9])([A-Z])", "$1 $2")
    };

    private IActionResult AdvancedMarketsValidationError(string fallback)
    {
        var fields = GetAdvancedMarketsInvalidFields(ModelState);
        var message = fields.Count > 0
            ? $"Please review these fields before saving: {string.Join("; ", fields)}."
            : fallback;

        _logger.LogWarning("Advanced Markets save validation failed for agent {AgentOid}. Message: {Message}",
            User.FindFirstValue("oid") ?? "(missing)",
            message);

        return BadRequest(message);
    }

        // Returns the effective agent OID (honors View-as-Agent / assistant resolution).
        private string GetAgentOidOrThrow()
        {
            var eff = _agentContext.EffectiveAgentOid;
            if (!string.IsNullOrWhiteSpace(eff)) return eff;

            var raw = Norm(
                User.FindFirstValue("oid")
                ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            );

            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Missing OID claim.");

            return raw;
        }

    // ✅ UPN is fine for email/audit only (NOT a security boundary)
    private string GetAgentUpnForAudit()
    {
        return (User.FindFirst("preferred_username")?.Value
             ?? User.FindFirst("upn")?.Value
             ?? User.Identity?.Name
             ?? "").Trim();
    }

    private string[] GetAgentIdCandidates(string agentOid)
    {
        var set = IdentityKey.NormalizeSet(User.GetUserIdCandidates());
        var effectiveKey = IdentityKey.Normalize(agentOid);
        if (!string.IsNullOrWhiteSpace(effectiveKey))
        {
            set.Add(effectiveKey);
        }

        return set.ToArray();
    }

    private Task<bool> AgentOwnsClientAsync(string agentOid, string clientUserId, CancellationToken ct = default)
        => _db.AgentOwnsClientAsync(agentOid, clientUserId, GetAgentUpnForAudit(), GetAgentIdCandidates(agentOid), ct);

    private string GetAgentDisplayName()
    {
        var name = (User.FindFirst("name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var given = (User.FindFirst("given_name")?.Value ?? User.FindFirst(ClaimTypes.GivenName)?.Value ?? "").Trim();
        var surname = (User.FindFirst("family_name")?.Value ?? User.FindFirst(ClaimTypes.Surname)?.Value ?? "").Trim();
        var combined = ($"{given} {surname}").Trim();
        if (!string.IsNullOrWhiteSpace(combined))
            return combined;

        var upn = GetAgentUpnForAudit();
        return string.IsNullOrWhiteSpace(upn) ? "Agent" : upn;
    }

    private string GetClientPortalBaseUrl()
    {
        return _config["Provisioning:ClientPortalBaseUrl"]?.Trim()
               ?? "https://localhost:5221";
    }

    private static string NormalizePipelineStage(string? stage)
        => ClientCrmMetaSerializer.NormalizePipelineStage(stage);

    private static string NormalizeRecordType(string? recordType, bool defaultToLead = true)
        => ClientCrmMetaSerializer.NormalizeRecordType(recordType, defaultToLead);

    private static bool HasPortalAccess(string? clientUserId)
        => Guid.TryParse(Norm(clientUserId), out _);

    private static bool IsPortalRecordType(string? recordType)
    {
        var normalized = NormalizeRecordType(recordType);
        return string.Equals(normalized, "Client", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "BusinessClient", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateLeadClientUserId()
        => $"lead-{Guid.NewGuid():N}";

    private static string DefaultPipelineStageForRecordType(string? recordType)
        => NormalizeRecordType(recordType) switch
        {
            "BusinessClient" => "BusinessClient",
            "Client" => "Client",
            _ => ClientCrmMeta.DefaultPipelineStage
        };

    private static string RecordTypeLabel(string? recordType)
        => NormalizeRecordType(recordType) switch
        {
            "BusinessClient" => "Business Client",
            "Client" => "Client",
            _ => "Lead"
        };

    private static string ResolveRecordType(string? clientUserId, ClientCrmMeta meta)
    {
        var explicitRecordType = NormalizeRecordType(meta.RecordType, defaultToLead: false);
        if (!string.IsNullOrWhiteSpace(explicitRecordType))
            return explicitRecordType;

        var stage = NormalizePipelineStage(meta.PipelineStage);
        if (string.Equals(stage, "BusinessClient", StringComparison.OrdinalIgnoreCase))
            return "BusinessClient";

        if (HasPortalAccess(clientUserId))
            return "Client";

        return "Lead";
    }

    private static string StageLabel(string? stage)
        => NormalizePipelineStage(stage) switch
        {
            "NewLead" => "Lead",
            "Opportunities" => "Opportunities",
            "Contacted" => "Contacted",
            "Qualified" => "Qualified",
            "Client" => "Clients",
            "BusinessClient" => "Business Clients",
            "MeetingScheduled" => "Meeting Scheduled",
            "ProposalSent" => "Proposal Sent",
            "ApplicationStarted" => "Application Started",
            "Submitted" => "Submitted",
            "ClosedLost" => "Not Moving Forward",
            "Nurture" => "Nurture",
            _ => "Lead"
        };

    private static string GenerateOneTimePassword(int length = 14)
    {
        const string lowers = "abcdefghijklmnopqrstuvwxyz";
        const string uppers = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string numbers = "0123456789";
        const string symbols = "!@#$%^&*()-_=+[]{};:,.?";
        var required = new[]
        {
            lowers[RandomNumberGenerator.GetInt32(lowers.Length)],
            uppers[RandomNumberGenerator.GetInt32(uppers.Length)],
            numbers[RandomNumberGenerator.GetInt32(numbers.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        }.ToList();

        var all = lowers + uppers + numbers + symbols;
        while (required.Count < length)
            required.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        for (var i = required.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (required[i], required[j]) = (required[j], required[i]);
        }

        return new string(required.ToArray());
    }

    private static string NormalizeWaitingOn(string? value)
        => ClientCrmMetaSerializer.NormalizeWaitingOn(value);

    private static string WaitingOnLabel(string? value)
        => NormalizeWaitingOn(value) switch
        {
            "WaitingOnClient" => "Waiting On Client",
            "WaitingOnCarrier" => "Waiting On Carrier",
            "WaitingOnUnderwriting" => "Waiting On Underwriting",
            "WaitingOnDocs" => "Waiting On Docs",
            _ => "Waiting On Agent"
        };

    // Contact attempt definition:
    // - Count only explicit Call/Text/Email types
    // - Legacy/null type may count if Channel is Call/Text/Email (some older records stored channel only)
    // - Do NOT count meetings/notes/tasks/admin updates even if they carry an OutcomeCode
    private static bool IsContactAttempt(ClientCrmActivity activity)
    {
        var type = (activity.Type ?? "").Trim();
        var channel = (activity.Channel ?? "").Trim();
        bool isContactType = type.Equals("Call", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Text", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Email", StringComparison.OrdinalIgnoreCase);
        bool isLegacyChannel = string.IsNullOrWhiteSpace(type) &&
            (channel.Equals("Call", StringComparison.OrdinalIgnoreCase)
             || channel.Equals("Text", StringComparison.OrdinalIgnoreCase)
             || channel.Equals("Email", StringComparison.OrdinalIgnoreCase));
        return isContactType || isLegacyChannel;
    }

    private static int CompletedDocCount(ClientCrmDocChecklist docs)
        => new[] { docs.IdReceived, docs.AppSent, docs.AppSigned, docs.PolicyDelivered, docs.ReviewBooked }.Count(x => x);

    private static int CompletedOpportunityPlanningCount(ClientCrmOpportunityPlanningChecklist planning)
        => new[]
        {
            planning.LifeInsurance,
            planning.DisabilityIncome,
            planning.LongTermCare,
            planning.CriticalIllness,
            planning.TerminalIllness,
            planning.AnnuityRetirement,
            planning.MortgageProtection,
            planning.FinalExpense,
            planning.Medicare,
            planning.Health,
            planning.DentalVision,
            planning.HospitalIndemnity,
            planning.PersonalAuto,
            planning.HomeRenters,
            planning.UmbrellaLiability,
            planning.FloodEarthquake,
            planning.CommercialAuto,
            planning.GeneralLiability,
            planning.BusinessOwnersPolicy,
            planning.WorkersComp,
            planning.KeyPersonBuySell,
            planning.GroupBenefits
        }.Count(x => x);

    private static DateTime NextBusinessDay(DateTime from)
    {
        var next = from.Date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);

        return next;
    }

    private static string ToIsoDate(DateTime value) => value.ToString("yyyy-MM-dd");

    private static ClientCrmMeta EnsureMeta(ClientCrmMeta? meta)
    {
        meta ??= new ClientCrmMeta();
        meta.DocChecklist ??= new ClientCrmDocChecklist();
        meta.OpportunityPlanning ??= new ClientCrmOpportunityPlanningChecklist();
        meta.Collaboration ??= new ClientCrmCollaboration();
        meta.Collaboration.Watchers ??= new List<string>();
        meta.Collaboration.MentionNotes ??= new List<ClientCrmMentionNote>();
        meta.Activities ??= new List<ClientCrmActivity>();
        if (string.IsNullOrWhiteSpace(meta.MeetingTime)) meta.MeetingTime = "09:00";
        if (meta.MeetingDurationMinutes <= 0) meta.MeetingDurationMinutes = 30;
        if (string.IsNullOrWhiteSpace(meta.PipelineStage)) meta.PipelineStage = ClientCrmMeta.DefaultPipelineStage;
        if (meta.StageEnteredUtc == default) meta.StageEnteredUtc = DateTime.UtcNow;
        return meta;
    }

    private static object BuildQuickViewPayload(ClientProfile profile, ClientCrmMeta meta, TimeZoneInfo dialTimeZone, DateTime nowUtc)
    {
        var safeActivities = (meta.Activities ?? new List<ClientCrmActivity>()).Where(a => a != null).ToList();
        meta.Activities = safeActivities;
        var attemptCounts = CrmAttemptTracking.CountClientActivityAttempts(safeActivities, IsContactAttempt, nowUtc, dialTimeZone);
        var recordType = ResolveRecordType(profile.ClientUserId, meta);

        return new
        {
            clientUserId = profile.ClientUserId,
            portalAccessEnabled = HasPortalAccess(profile.ClientUserId),
            recordType,
            advancedMarketsEligible = IsBusinessClientRecordType(recordType),
            recordTypeLabel = RecordTypeLabel(recordType),
            crmStatus = string.IsNullOrWhiteSpace(profile.CrmStatus) ? "Lead" : profile.CrmStatus,
            crmPriority = string.IsNullOrWhiteSpace(profile.CrmPriority) ? "Normal" : profile.CrmPriority,
            email = profile.Email ?? "",
            phone = profile.Phone ?? "",
            dob = profile.DOB?.ToString("yyyy-MM-dd") ?? "",
            gender = meta.Gender ?? "",
            addressLine = meta.AddressLine ?? "",
            city = meta.City ?? "",
            state = meta.State ?? "",
            county = meta.County ?? "",
            zipCode = meta.ZipCode ?? "",
            phone2 = meta.Phone2 ?? "",
            age = meta.Age ?? "",
            btc = meta.Btc ?? "",
            mortgageLender = meta.MortgageLender ?? "",
            loanAmount = meta.LoanAmount ?? "",
            crmLastTouch = profile.CrmLastTouch?.ToString("yyyy-MM-dd"),
            crmNextDate = profile.CrmNextDate?.ToString("yyyy-MM-dd"),
            crmNextText = profile.CrmNextText ?? "",
            crmTags = profile.CrmTags ?? "",
            agentNotes = profile.AgentNotes ?? "",
            pipelineStage = meta.PipelineStage,
            pipelineOrder = meta.PipelineOrder,
            pipelineStageLabel = StageLabel(meta.PipelineStage),
            waitingOn = meta.WaitingOn,
            waitingOnLabel = WaitingOnLabel(meta.WaitingOn),
            pinnedBrief = meta.PinnedBrief ?? "",
            stageEnteredUtc = meta.StageEnteredUtc,
            stageAgeDays = Math.Max(0, (nowUtc.Date - meta.StageEnteredUtc.Date).Days),
            meetingLocation = meta.MeetingLocation ?? "",
            zoomJoinUrl = meta.ZoomJoinUrl ?? "",
            usePersonalZoomLink = meta.UsePersonalZoomLink,
            meetingTime = meta.MeetingTime ?? "09:00",
            meetingDurationMinutes = meta.MeetingDurationMinutes,
            lastCalendarEventId = meta.LastCalendarEventId ?? "",
            lastCalendarEventWebLink = meta.LastCalendarEventWebLink ?? "",
            lastContactChannel = meta.LastContactChannel ?? "",
            attemptsToday = attemptCounts.Today,
            attemptsThisWeek = attemptCounts.Week,
            attemptsThisMonth = attemptCounts.Month,
            attemptsThisYear = attemptCounts.Year,
            attemptsLifetime = attemptCounts.Lifetime,
            docChecklist = new
            {
                idReceived = meta.DocChecklist.IdReceived,
                appSent = meta.DocChecklist.AppSent,
                appSigned = meta.DocChecklist.AppSigned,
                policyDelivered = meta.DocChecklist.PolicyDelivered,
                reviewBooked = meta.DocChecklist.ReviewBooked,
                completedCount = CompletedDocCount(meta.DocChecklist)
            },
            opportunityPlanning = new
            {
                lifeInsurance = meta.OpportunityPlanning.LifeInsurance,
                disabilityIncome = meta.OpportunityPlanning.DisabilityIncome,
                longTermCare = meta.OpportunityPlanning.LongTermCare,
                criticalIllness = meta.OpportunityPlanning.CriticalIllness,
                terminalIllness = meta.OpportunityPlanning.TerminalIllness,
                annuityRetirement = meta.OpportunityPlanning.AnnuityRetirement,
                mortgageProtection = meta.OpportunityPlanning.MortgageProtection,
                finalExpense = meta.OpportunityPlanning.FinalExpense,
                medicare = meta.OpportunityPlanning.Medicare,
                health = meta.OpportunityPlanning.Health,
                dentalVision = meta.OpportunityPlanning.DentalVision,
                hospitalIndemnity = meta.OpportunityPlanning.HospitalIndemnity,
                personalAuto = meta.OpportunityPlanning.PersonalAuto,
                homeRenters = meta.OpportunityPlanning.HomeRenters,
                umbrellaLiability = meta.OpportunityPlanning.UmbrellaLiability,
                floodEarthquake = meta.OpportunityPlanning.FloodEarthquake,
                commercialAuto = meta.OpportunityPlanning.CommercialAuto,
                generalLiability = meta.OpportunityPlanning.GeneralLiability,
                businessOwnersPolicy = meta.OpportunityPlanning.BusinessOwnersPolicy,
                workersComp = meta.OpportunityPlanning.WorkersComp,
                keyPersonBuySell = meta.OpportunityPlanning.KeyPersonBuySell,
                groupBenefits = meta.OpportunityPlanning.GroupBenefits,
                completedCount = CompletedOpportunityPlanningCount(meta.OpportunityPlanning)
            },
            collaboration = new
            {
                owner = meta.Collaboration.Owner ?? "",
                watchers = meta.Collaboration.Watchers,
                mentionNotes = meta.Collaboration.MentionNotes
            },
            activities = safeActivities
                .OrderByDescending(x => x.Date)
                .ThenByDescending(x => x.CreatedUtc)
                .ToList()
        };
    }

    private sealed record OutcomePlan(
        string PipelineStage,
        string WaitingOn,
        string? CrmStatus,
        string NextActionText,
        DateTime NextActionDate,
        string ActivityType,
        string ActivityNote,
        string ContactChannel,
        bool ClearMeetingDetails = false);

    private static OutcomePlan? BuildOutcomePlan(string? rawOutcome)
    {
        var today = DateTime.UtcNow.Date;
        var nextBusinessDay = NextBusinessDay(today);

        return (rawOutcome ?? "").Trim() switch
        {
            "NoAnswer" => new OutcomePlan(
                "NewLead",
                "WaitingOnAgent",
                "Lead",
                "Retry call",
                nextBusinessDay,
                "Call",
                "No answer. Retry call on the next business day.",
                "Call"),
            "LeftVM" => new OutcomePlan(
                "Contacted",
                "WaitingOnClient",
                "Prospect",
                "VM follow-up call/text",
                nextBusinessDay,
                "Call",
                "Left voicemail. Queue a follow-up touch.",
                "Call"),
            "Spoke" => new OutcomePlan(
                "Contacted",
                "WaitingOnAgent",
                "Prospect",
                "Follow up from live conversation",
                today.AddDays(1),
                "Call",
                "Spoke live. Keep momentum and follow up.",
                "Call"),
            "Booked" => new OutcomePlan(
                "MeetingScheduled",
                "WaitingOnClient",
                "Active",
                "Confirm meeting and prep notes",
                today,
                "Meeting",
                "Booked a meeting. Sync details to Outlook.",
                "Meeting"),
            "NeedsDocs" => new OutcomePlan(
                "ApplicationStarted",
                "WaitingOnDocs",
                "Active",
                "Collect required documents",
                NextBusinessDay(today.AddDays(1)),
                "Note",
                "Needs documents before the case can progress.",
                "Docs"),
            "BusinessClient" or "ClosedWon" => new OutcomePlan(
                "BusinessClient",
                "WaitingOnAgent",
                "Active",
                "Post-sale review and retention touch",
                today.AddDays(7),
                "Note",
                "Moved into the Business Clients servicing bucket. Schedule review and retention follow-up.",
                "System"),
            "ClosedLost" => new OutcomePlan(
                "ClosedLost",
                "WaitingOnAgent",
                "Dormant",
                "Rescue or re-entry follow-up",
                today.AddDays(14),
                "Note",
                "Closed lost. Queue a rescue or nurture touch.",
                "System"),
            _ => null
        };
    }

    private async Task<ClientProfile?> GetOwnedClientProfileAsync(string agentOid, string clientUserId)
    {
        var agentOidNorm = Norm(agentOid).ToLowerInvariant();
        var clientUserIdNorm = Norm(clientUserId).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(agentOidNorm) || string.IsNullOrWhiteSpace(clientUserIdNorm))
            return null;

        var linked = await _db.AgentClients.AnyAsync(x =>
            (x.AgentUserId ?? "").ToLower() == agentOidNorm &&
            (x.ClientUserId ?? "").ToLower() == clientUserIdNorm);

        if (!linked)
            return null;

        return await _db.ClientProfiles.FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == clientUserIdNorm);
    }

    private async Task<ClientProfile?> GetOwnedClientProfileAsync(string agentOid, Guid clientProfileId)
    {
        var agentOidNorm = Norm(agentOid).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(agentOidNorm) || clientProfileId == Guid.Empty)
            return null;

        var profile = await _db.ClientProfiles.FirstOrDefaultAsync(x => x.Id == clientProfileId);
        if (profile == null)
            return null;

        var clientUserIdNorm = Norm(profile.ClientUserId).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(clientUserIdNorm))
            return null;

        var linked = await _db.AgentClients.AnyAsync(x =>
            (x.AgentUserId ?? "").ToLower() == agentOidNorm &&
            (x.ClientUserId ?? "").ToLower() == clientUserIdNorm);

        return linked ? profile : null;
    }

    private static bool IsToday(DateTime? value)
        => value.HasValue && value.Value.Date == DateTime.UtcNow.Date;

    private static bool IsOverdue(DateTime? value)
        => value.HasValue && value.Value.Date < DateTime.UtcNow.Date;

    private static bool MatchesQueue(ClientListItemViewModel item, string queueKey)
        => queueKey switch
        {
            "callsnow" => (item.CrmPriority == "High" || item.CrmPriority == "Urgent")
                && (IsToday(item.CrmNextDate) || IsOverdue(item.CrmNextDate)),
            "today" => IsToday(item.CrmNextDate),
            "overdue" => IsOverdue(item.CrmNextDate),
            "meetings" => string.Equals(item.PipelineStage, "MeetingScheduled", StringComparison.OrdinalIgnoreCase),
            "waitingclient" => string.Equals(item.WaitingOn, "WaitingOnClient", StringComparison.OrdinalIgnoreCase),
            "waitingcarrier" => string.Equals(item.WaitingOn, "WaitingOnCarrier", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static (string Title, string Description, string Rule) QueueMeta(string queueKey)
        => queueKey switch
        {
            "callsnow" => (
                "Calls Now",
                "Priority follow-up calls that should happen immediately.",
                "High or Urgent priority, with the next action due today or already overdue."
            ),
            "today" => (
                "Due Today",
                "Touches due today and ready for execution.",
                "Any record with a Next Action Date set to today."
            ),
            "overdue" => (
                "Overdue",
                "Rescue this list before it gets stale.",
                "Any record with a Next Action Date in the past."
            ),
            "meetings" => (
                "Meetings",
                "Meeting-stage clients with event execution pressure.",
                "Any record currently in the Meeting Scheduled pipeline stage."
            ),
            "waitingclient" => (
                "Waiting On Client",
                "Clients who owe the next move back to you.",
                "Any record whose Waiting On field is set to Waiting On Client."
            ),
            "waitingcarrier" => (
                "Waiting On Carrier",
                "Cases blocked externally and needing visibility.",
                "Any record whose Waiting On field is set to Waiting On Carrier."
            ),
            _ => (
                "Queue",
                "Manage this queue directly.",
                "Filtered records assigned to this queue."
            )
        };

    private async Task<List<ClientListItemViewModel>> GetOwnedClientListItemsAsync(string agentOid, string? search = null)
    {
        _logger.LogInformation("GetOwnedClientListItemsAsync starting for agent {AgentOid} search {Search}.", agentOid, search);

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        var q = NormLower(search);

        var query =
            _db.AgentClients
              .Where(ac => ac.AgentUserId == agentOid)
              .Join(_db.ClientProfiles,
                  ac => ac.ClientUserId,
                  cp => cp.ClientUserId,
                  (ac, cp) => cp)
              .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var tokens = q
                .Replace(",", " ")
                .Replace("-", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            query = query.Where(x =>
                tokens.All(t =>
                    ((x.FirstName ?? "").ToLower()).Contains(t) ||
                    ((x.LastName ?? "").ToLower()).Contains(t) ||
                    ((x.Email ?? "").ToLower()).Contains(t)
                )
            );
        }

        var clients = await query
            .OrderBy(x => x.LastName)
            .ThenBy(x => x.FirstName)
            .ToListAsync();

        var clientIds = clients
            .Select(x => x.ClientUserId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        var productionLookup = await _db.ProductionRecords
            .Where(p => p.AgentUserId == agentOid
                        && p.Side == ProductionSide.Client
                        && p.ClientUserId != null
                        && clientIds.Contains(p.ClientUserId))
            .GroupBy(p => p.ClientUserId!)
            .Select(g => new
            {
                ClientUserId = g.Key,
                Paid = g.Where(p => p.Status == ProductionStatus.Paid)
                        .Select(p => (decimal?)p.Amount).Sum() ?? 0,
                Personal = g.Select(p => (decimal?)p.PersonalAmount).Sum() ?? 0,
                LatestStatus = g.OrderByDescending(p => p.UpdatedUtc).Select(p => p.Status).FirstOrDefault(),
                LatestAmount = g.OrderByDescending(p => p.UpdatedUtc).Select(p => p.Amount).FirstOrDefault()
            })
            .ToListAsync();

        var productionDict = productionLookup.ToDictionary(x => x.ClientUserId, x => x, StringComparer.OrdinalIgnoreCase);

        var mapped = new List<ClientListItemViewModel>();

        foreach (var x in clients)
        {
            try
            {
                var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(x.CrmNotes));
                var recordType = ResolveRecordType(x.ClientUserId, meta);
                productionDict.TryGetValue(x.ClientUserId, out var prod);
                var paid = prod?.Paid ?? 0;
                var personal = prod?.Personal ?? 0;
                var allByEmail = clients.Count(cp =>
                    !string.IsNullOrWhiteSpace(cp.Email) &&
                    string.Equals(cp.Email, x.Email, StringComparison.OrdinalIgnoreCase));
                var allByPhone = clients.Count(cp =>
                    !string.IsNullOrWhiteSpace(cp.Phone) &&
                    string.Equals(cp.Phone, x.Phone, StringComparison.OrdinalIgnoreCase));
                var allByHousehold = clients.Count(cp =>
                    !string.IsNullOrWhiteSpace(cp.SignificantOtherEmail) &&
                    !string.IsNullOrWhiteSpace(x.SignificantOtherEmail) &&
                    string.Equals(cp.SignificantOtherEmail, x.SignificantOtherEmail, StringComparison.OrdinalIgnoreCase));
                var attemptCounts = CrmAttemptTracking.CountClientActivityAttempts(meta.Activities, IsContactAttempt, nowUtc, dialTimeZone);

                mapped.Add(new ClientListItemViewModel
                {
                    Id = x.Id,
                    ClientUserId = x.ClientUserId,
                    FirstName = x.FirstName,
                    LastName = x.LastName,
                    Email = x.Email,
                    Phone = x.Phone,
                    Phone2 = meta.Phone2,
                    Age = meta.Age,
                    Btc = meta.Btc,
                    RecordType = recordType,
                    CrmStatus = string.IsNullOrWhiteSpace(x.CrmStatus) ? "Lead" : x.CrmStatus!,
                    CrmPriority = string.IsNullOrWhiteSpace(x.CrmPriority) ? "Normal" : x.CrmPriority!,
                    CrmLastTouch = x.CrmLastTouch,
                    CrmNextDate = x.CrmNextDate,
                    CrmNextText = x.CrmNextText,
                    CrmTags = x.CrmTags,
                    AgentNotes = x.AgentNotes,
                    AddressLine = meta.AddressLine,
                    City = meta.City,
                    State = meta.State,
                    ZipCode = meta.ZipCode,
                    County = meta.County,
                    Gender = meta.Gender,
                    DOB = meta.DOB,
                    MortgageLender = meta.MortgageLender,
                    LoanAmount = meta.LoanAmount,
                    PipelineStage = meta.PipelineStage,
                    PipelineOrder = meta.PipelineOrder,
                    MeetingLocation = meta.MeetingLocation,
                    ZoomJoinUrl = meta.ZoomJoinUrl,
                    UsePersonalZoomLink = meta.UsePersonalZoomLink,
                    MeetingTime = meta.MeetingTime,
                    MeetingDurationMinutes = meta.MeetingDurationMinutes,
                    WaitingOn = meta.WaitingOn,
                    PinnedBrief = meta.PinnedBrief,
                    StageEnteredUtc = meta.StageEnteredUtc,
                    StageAgeDays = Math.Max(0, (nowUtc.Date - meta.StageEnteredUtc.Date).Days),
                    AttemptsToday = attemptCounts.Today,
                    AttemptsThisWeek = attemptCounts.Week,
                    AttemptsThisMonth = attemptCounts.Month,
                    AttemptsYear = attemptCounts.Year,
                    AttemptsLifetime = attemptCounts.Lifetime,
                    LastContactChannel = meta.LastContactChannel,
                    DocChecklistCompletedCount = CompletedDocCount(meta.DocChecklist),
                    HasDuplicateEmail = allByEmail > 1,
                    HasDuplicatePhone = allByPhone > 1,
                    HasDuplicateHousehold = allByHousehold > 1,
                    AssignedOwner = meta.Collaboration.Owner,
                    WatchersCsv = string.Join(", ", meta.Collaboration.Watchers),
                    ProductionStatus = prod?.LatestStatus.ToString() ?? "",
                    ProductionAmount = prod?.LatestAmount ?? 0,
                    PaidAmount = paid,
                    PersonalAmount = personal
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetOwnedClientListItemsAsync failed mapping profile ClientUserId={ClientUserId}, Email={Email}, FirstName={FirstName}, LastName={LastName}",
                    x.ClientUserId, x.Email, x.FirstName, x.LastName);
                throw;
            }
        }

        var ordered = mapped
            .OrderBy(x => x.PipelineStage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PipelineOrder)
            .ThenBy(x => x.LastName)
            .ThenBy(x => x.FirstName)
            .ToList();

        _logger.LogInformation("GetOwnedClientListItemsAsync returning {Count} items for agent {AgentOid}.", ordered.Count, agentOid);

        return ordered;
    }

    private void PrepareEditView(EditClientViewModel model, string? returnUrl)
    {
        model.RecordType = NormalizeRecordType(model.RecordType);

        ViewData["Title"] = $"Edit {RecordTypeLabel(model.RecordType)}";
        ViewBag.ClientUserId = model.ClientUserId;
        ViewBag.ClientDisplayName = $"{model.FirstName} {model.LastName}".Trim();
        ViewBag.ClientRecordType = model.RecordType;
        ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? (Url.Action(nameof(Index), "Clients") ?? "/Clients")
            : returnUrl;
    }

    // =====================================================================
    // GET: /Clients
    // =====================================================================
    [HttpGet]
    public async Task<IActionResult> Index(string? search)
    {
        string agentOid;
        try
        {
            agentOid = GetAgentOidOrThrow();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clients/Index failed while resolving agent OID.");
            return Challenge();
        }

        try
        {
            var vm = await GetOwnedClientListItemsAsync(agentOid, search);

            ViewBag.ClientPortalBaseUrl = GetClientPortalBaseUrl();
            ViewBag.Search = search ?? "";
            ViewData["ProductionTotals"] = await _production.GetAgentTotalsAsync(agentOid, ProductionSide.Client);

            _logger.LogInformation("Clients/Index loaded {Count} records for agent {AgentOid}.", vm?.Count ?? 0, agentOid);

            return View(vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clients/Index crashed for agent {AgentOid} search {Search}.", agentOid, search);
            throw;
        }
    }

    [HttpGet]
    public async Task<IActionResult> Queue(string queue)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var queueKey = NormLower(queue);
        var meta = QueueMeta(queueKey);
        var items = (await GetOwnedClientListItemsAsync(agentOid))
            .Where(x => MatchesQueue(x, queueKey))
            .OrderBy(x => x.CrmNextDate ?? DateTime.MaxValue)
            .ThenBy(x => x.LastName)
            .ThenBy(x => x.FirstName)
            .ToList();

        ViewBag.ClientPortalBaseUrl = GetClientPortalBaseUrl();

        return View(new ClientQueuePageViewModel
        {
            QueueKey = queueKey,
            QueueTitle = meta.Title,
            QueueDescription = meta.Description,
            QueueRule = meta.Rule,
            Items = items
        });
    }

    [HttpGet]
    public async Task<IActionResult> MyDaySnapshot()
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var items = await GetOwnedClientListItemsAsync(agentOid);

        var queueKeys = new[] { "callsnow", "today", "overdue", "meetings", "waitingclient", "waitingcarrier" };
        var queues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in queueKeys)
        {
            var matchingIds = items
                .Where(x => MatchesQueue(x, key))
                .Select(x => x.ClientUserId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            queues[key] = new
            {
                count = matchingIds.Count,
                ids = matchingIds
            };
        }

        return Json(new
        {
            generatedUtc = DateTime.UtcNow,
            queues
        });
    }

    // =====================================================================
    // POST: /Clients/ImportLeadsCsv
    // =====================================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB safety cap
    public async Task<IActionResult> ImportLeadsCsv(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Upload a CSV file exported from Excel." });

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var agentUpn = GetAgentUpnForAudit();
        if (string.IsNullOrWhiteSpace(agentUpn))
            return Forbid();

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await ImportLeadsFromCsvAsync(stream, agentOid, agentUpn);
            return Json(new
            {
                imported = result.Imported,
                updated = result.Updated,
                skipped = result.Skipped,
                errors = result.Errors
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lead CSV import failed for AgentOid={AgentOid}", agentOid);
            return BadRequest(new { error = $"Import failed: {ex.Message}" });
        }
    }

    // =====================================================================
    // GET: /Clients/ImportTemplateCsv
    // =====================================================================
    [HttpGet]
    public IActionResult ImportTemplateCsv()
    {
        const string sample = "First Name,Last Name,Address,City,State,County,Zip Code,Age,DOB,M/F,Lender,Loan,Phone #,Phone # 2\nAlex,Lead,123 Main St,Phoenix,AZ,Maricopa,85001,45,1981-05-04,M,ACME Bank,250000,4805551234,4805555678";
        var bytes = System.Text.Encoding.UTF8.GetBytes(sample);
        return File(bytes, "text/csv", "legend-leads-template.csv");
    }

    [HttpGet]
    public async Task<IActionResult> Actions(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("Client id required");
        string agentId;
        try { agentId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        if (!await AgentOwnsClientAsync(agentId, id))
            return Forbid();

        var actions = await _execution.GetByRelatedAsync(RelatedEntityType.Client, id, agentId);
        ViewBag.ClientId = id;
        return PartialView("~/Views/Clients/_ClientActionsTab.cshtml", actions);
    }

    [HttpGet]
    public async Task<IActionResult> Commitments(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("Client id required");
        string agentId;
        try { agentId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        if (!await AgentOwnsClientAsync(agentId, id))
            return Forbid();

        try
        {
            var commitments = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Client, id, agentId);
            ViewBag.ClientId = id;
            ViewBag.AgentId = agentId;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", commitments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load commitments for client {ClientId}", id);
            ViewBag.ClientId = id;
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateAction([FromForm] CreateClientActionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("ClientId and Title required");

        string ownerId;
        try { ownerId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        if (!await AgentOwnsClientAsync(ownerId, req.ClientId))
            return Forbid();

        var action = BuildClientAction(req, ownerId);

        await _execution.CreateActionAsync(action);
        return RedirectToAction(nameof(Actions), new { id = req.ClientId });
    }

    [HttpPost]
    public async Task<IActionResult> CreateCommitment([FromForm] CreateClientCommitmentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.PromiseText))
            return BadRequest("ClientId and Promise are required");
        if (req.DueDateUtc == null) return BadRequest("Due date is required");

        string agentId;
        try { agentId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        if (!await AgentOwnsClientAsync(agentId, req.ClientId))
            return Forbid();

        var createRequest = new CommitmentCreateRequest(
            RelatedEntityType.Client,
            req.ClientId.Trim(),
            ActionOwnerType.Agent,
            agentId,
            ActionOwnerType.Client,
            req.ClientId.Trim(),
            req.PromiseText.Trim(),
            req.DueDateUtc.Value.ToUniversalTime(),
            agentId
        );

        try
        {
            await _commitments.CreateCommitmentAsync(createRequest);
            return RedirectToAction(nameof(Commitments), new { id = req.ClientId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create commitment for client {ClientId}", req.ClientId);
            ViewBag.ClientId = req.ClientId;
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    private static ActionItem BuildClientAction(CreateClientActionRequest req, string ownerId)
        => new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Client,
            RelatedEntityId = req.ClientId.Trim(),
            Title = req.Title.Trim(),
            Description = req.Description?.Trim() ?? string.Empty,
            OwnerType = ActionOwnerType.Agent,
            OwnerId = ownerId,
            EffectiveAgentOid = ownerId,
            DueDateUtc = req.DueDateUtc,
            Status = ActionStatus.Planned,
            Priority = req.Priority,
            ActionSurface = (req.ShowInCommandCenter || req.ShowInDashboard || req.IncludeInDashboard)
                ? ActionSurface.CommandCenter
                : ActionSurface.CrmOnly,
            Source = "client-manual",
            SourceRef = $"{req.ClientId}-manual",
            CreatedBy = ownerId,
            CreatedUtc = DateTime.UtcNow
        };

    [HttpPost]
    public async Task<IActionResult> FulfillCommitment(Guid id)
    {
        if (id == Guid.Empty) return BadRequest("Commitment id required");

        string agentId;
        try { agentId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        try
        {
            var commit = await _commitments.GetByIdForActorAsync(id, agentId);
            if (commit == null) return NotFound();
            if (commit.RelatedEntityType != RelatedEntityType.Client) return BadRequest("Only client commitments are supported here.");

            var updated = await _commitments.FulfillCommitmentAsync(id, agentId);
            if (updated == null) return NotFound();

            var refreshed = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Client, commit.RelatedEntityId, agentId);
            ViewBag.ClientId = commit.RelatedEntityId;
            ViewBag.AgentId = agentId;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fulfill client commitment {CommitmentId}", id);
            ViewBag.ClientId = id.ToString();
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    [HttpPost]
    public async Task<IActionResult> BreakCommitment(Guid id)
    {
        if (id == Guid.Empty) return BadRequest("Commitment id required");

        string agentId;
        try { agentId = NormLower(GetAgentOidOrThrow()); }
        catch { return Challenge(); }

        try
        {
            var commit = await _commitments.GetByIdForActorAsync(id, agentId);
            if (commit == null) return NotFound();
            if (commit.RelatedEntityType != RelatedEntityType.Client) return BadRequest("Only client commitments are supported here.");

            var updated = await _commitments.BreakCommitmentAsync(id, agentId);
            if (updated == null) return NotFound();

            var refreshed = await _commitments.GetByEntityForActorAsync(RelatedEntityType.Client, commit.RelatedEntityId, agentId);
            ViewBag.ClientId = commit.RelatedEntityId;
            ViewBag.AgentId = agentId;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to break client commitment {CommitmentId}", id);
            ViewBag.ClientId = id.ToString();
            ViewBag.AgentId = agentId;
            ViewBag.CommitmentsError = CommitmentsUnavailableMessage;
            return PartialView("~/Views/Clients/_ClientCommitmentsTab.cshtml", Enumerable.Empty<Commitment>());
        }
    }

    private static Dictionary<string, int> BuildHeaderMap(string[] headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headerRow.Length; i++)
        {
            var key = CleanHeader(headerRow[i]);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!map.ContainsKey(key))
                map[key] = i;
        }

        return map;
    }

    private static string CleanHeader(string? value)
    {
        var cleaned = new string((value ?? "")
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        return cleaned;
    }

    private static string? GetField(string[] row, Dictionary<string, int> headerMap, bool hasHeader, int fallbackIndex, params string[] aliases)
    {
        if (hasHeader)
        {
            foreach (var alias in aliases)
            {
                var key = CleanHeader(alias);
                if (headerMap.TryGetValue(key, out var idx) && idx < row.Length)
                    return Norm(row[idx]);
            }
        }
        else
        {
            if (fallbackIndex < row.Length)
                return Norm(row[fallbackIndex]);
        }

        return null;
    }

    private async Task<LeadImportResult> ImportLeadsFromCsvAsync(Stream stream, string agentOid, string agentUpn)
    {
        var result = new LeadImportResult();
        var now = DateTime.UtcNow;

        var existingEmails = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .Select(x => x.Email)
            .ToListAsync();

        var existingPhones = await _db.ClientProfiles
            .AsNoTracking()
            .Where(x => !string.IsNullOrWhiteSpace(x.Phone))
            .Select(x => x.Phone)
            .ToListAsync();
        var existingProfiles = await _db.ClientProfiles.ToListAsync();

        var emailSet = new HashSet<string>(existingEmails, StringComparer.OrdinalIgnoreCase);
        var phoneSet = new HashSet<string>(existingPhones
            .Select(NormalizePhoneKey)
            .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);

        var phoneLookup = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in existingProfiles)
        {
            var key = NormalizePhoneKey(profile.Phone);
            if (!string.IsNullOrWhiteSpace(key) && !phoneLookup.ContainsKey(key))
                phoneLookup[key] = profile;
        }
        var emailLookup = existingProfiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .ToDictionary(p => (p.Email ?? "").Trim().ToLowerInvariant(), p => p, StringComparer.OrdinalIgnoreCase);

        var batchEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var newProfiles = new List<ClientProfile>();
        var newLinks = new List<AgentClient>();

        using var parser = new TextFieldParser(stream);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",", ";", "\t");
        parser.HasFieldsEnclosedInQuotes = true;

        var headerRow = parser.ReadFields();
        if (headerRow == null || headerRow.Length == 0)
        {
            result.Errors.Add("The CSV file is empty.");
            return result;
        }

        var headerMap = BuildHeaderMap(headerRow);
        var hasHeader = headerMap.Count > 0;
        var rows = new List<string[]>();

        if (!hasHeader)
            rows.Add(headerRow);

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields != null) rows.Add(fields);
        }

        if (rows.Count > 500)
        {
            result.Errors.Add("Limit 500 rows per upload. Only the first 500 rows were processed.");
            rows = rows.Take(500).ToList();
        }

        var rowNumber = hasHeader ? 2 : 1;
        foreach (var row in rows)
        {
            try
            {
                if (row.Length < 14)
                {
                    result.Skipped++;
                    result.Errors.Add($"Row {rowNumber}: expected 14 columns (First, Last, Address, City, State, County, Zip, Age, DOB, M/F, Lender, Loan, Phone #, Phone #2).");
                    rowNumber++;
                    continue;
                }

                var first = GetField(row, headerMap, hasHeader, 0, "firstname", "first", "fname");
                var last = GetField(row, headerMap, hasHeader, 1, "lastname", "last", "lname");
                var address = GetField(row, headerMap, hasHeader, 2, "address");
                var city = GetField(row, headerMap, hasHeader, 3, "city");
                var state = GetField(row, headerMap, hasHeader, 4, "state", "st");
                var county = GetField(row, headerMap, hasHeader, 5, "county", "parish");
                var zip = GetField(row, headerMap, hasHeader, 6, "zip", "zipcode", "postal");
                var ageRaw = GetField(row, headerMap, hasHeader, 7, "age");
                var dobRaw = GetField(row, headerMap, hasHeader, 8, "dob", "birthdate", "dateofbirth");
                var gender = GetField(row, headerMap, hasHeader, 9, "mf", "gender", "sex");
                var lender = GetField(row, headerMap, hasHeader, 10, "lender", "bank");
                var loanRaw = GetField(row, headerMap, hasHeader, 11, "loan", "loanamount", "amount");
                var phone = GetField(row, headerMap, hasHeader, 12, "phone", "phone1", "primaryphone", "mobile", "cell");
                var phone2 = GetField(row, headerMap, hasHeader, 13, "phone2", "altphone", "secondaryphone", "mobile2", "cell2");

                if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last) && string.IsNullOrWhiteSpace(phone))
                {
                    result.Skipped++;
                    result.Errors.Add($"Row {rowNumber}: missing first, last, and phone.");
                    rowNumber++;
                    continue;
                }

                var phoneKey = NormalizePhoneKey(phone);
                var phone2Key = NormalizePhoneKey(phone2);

                DateTime? dob = null;
                if (DateTime.TryParse(dobRaw, out var parsedDob))
                    dob = parsedDob.Date;

                var meta = new ClientCrmMeta
                {
                    RecordType = "Lead",
                    PipelineStage = ClientCrmMeta.DefaultPipelineStage,
                    PipelineOrder = now.Ticks,
                    StageEnteredUtc = now,
                    WaitingOn = ClientCrmMeta.DefaultWaitingOn,
                    DOB = dob,
                    Collaboration = new ClientCrmCollaboration
                    {
                        Owner = agentUpn
                    },
                    Activities = new List<ClientCrmActivity>
                    {
                        new ClientCrmActivity
                        {
                            Type = "Note",
                            Date = ToIsoDate(now),
                            Note = "Imported from CSV",
                            CreatedBy = agentUpn,
                            IsSystem = true
                        }
                    }
                };

                var notes = new List<string>();
                meta.AddressLine = address;
                meta.City = city;
                meta.State = state;
                meta.County = county;
                meta.ZipCode = zip;
                meta.MortgageLender = lender;
                meta.LoanAmount = loanRaw;
                meta.Gender = gender;
                void add(string label, string? val)
                {
                    if (!string.IsNullOrWhiteSpace(val))
                        notes.Add($"{label}: {val}");
                }

                add("Address", string.Join(", ", new[] { address, city, state, zip }.Where(x => !string.IsNullOrWhiteSpace(x))));
                add("County", county);
                add("Age", ageRaw);
                add("DOB", dob?.ToString("yyyy-MM-dd") ?? dobRaw);
                add("Gender", gender);
                add("Lender", lender);
                add("Loan", loanRaw);
                add("Alt Phone", phone2);

                var clientUserId = CreateLeadClientUserId();
                // Email is required + unique in the DB. Imported lead rows usually lack an email
                // address, so generate a deterministic placeholder per record to satisfy the
                // constraint without colliding on "".
                var placeholderEmail = $"{clientUserId}@leads.local";
                var profile = new ClientProfile
                {
                    ClientUserId = clientUserId,
                    FirstName = first ?? "",
                    LastName = last ?? "",
                    Email = placeholderEmail,
                    NormalizedEmail = placeholderEmail.ToLowerInvariant(),
                    Phone = FormatPhoneDisplay(phone),
                    DOB = dob,
                    MaritalStatus = "",
                    CrmStatus = "Lead",
                    CrmPriority = "Normal",
                    CrmTags = NormalizeTags(state),
                    CrmNotes = ClientCrmMetaSerializer.Serialize(meta),
                    AgentNotes = string.Join("; ", notes.Where(n => !string.IsNullOrWhiteSpace(n))),
                    CreatedUtc = now,
                    UpdatedUtc = now
                };

                newProfiles.Add(profile);
                newLinks.Add(new AgentClient
                {
                    AgentUserId = agentOid,
                    ClientUserId = clientUserId,
                    AgentUpn = agentUpn,
                    CreatedUtc = now
                });

                if (!string.IsNullOrWhiteSpace(phoneKey)) phoneSet.Add(phoneKey);
                if (!string.IsNullOrWhiteSpace(phone2Key)) phoneSet.Add(phone2Key);
                if (!string.IsNullOrWhiteSpace(phoneKey) && !phoneLookup.ContainsKey(phoneKey))
                    phoneLookup[phoneKey] = profile;

                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Skipped++;
                result.Errors.Add($"Row {rowNumber}: {ex.Message}");
            }
            finally
            {
                rowNumber++;
            }
        }

        if (newProfiles.Count == 0)
            return result;

        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.ClientProfiles.AddRange(newProfiles);
        _db.AgentClients.AddRange(newLinks);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return result;
    }

    private void UpdateExistingFromImport(
        ClientProfile profile,
        string agentOid,
        string? first,
        string? last,
        ClientCrmMeta metaFromFile,
        List<string> notes,
        DateTime? dob,
        string? phone,
        string phoneKey,
        string? county,
        string? address,
        string? city,
        string? state,
        string? zip,
        string? lender,
        string? loanRaw,
        string agentUpn,
        DateTime now)
    {
        profile.FirstName = string.IsNullOrWhiteSpace(profile.FirstName) ? (first ?? profile.FirstName) : profile.FirstName;
        profile.LastName = string.IsNullOrWhiteSpace(profile.LastName) ? (last ?? profile.LastName) : profile.LastName;

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        meta.DocChecklist ??= new ClientCrmDocChecklist();
        meta.OpportunityPlanning ??= new ClientCrmOpportunityPlanningChecklist();
        if (dob.HasValue && !profile.DOB.HasValue)
            profile.DOB = dob;
        if (dob.HasValue && !meta.DOB.HasValue)
            meta.DOB = dob;
        if (!string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(profile.Phone))
            profile.Phone = FormatPhoneDisplay(phone);
        meta.AddressLine = string.IsNullOrWhiteSpace(meta.AddressLine) ? address ?? meta.AddressLine : meta.AddressLine;
        meta.City = string.IsNullOrWhiteSpace(meta.City) ? city ?? meta.City : meta.City;
        meta.State = string.IsNullOrWhiteSpace(meta.State) ? state ?? meta.State : meta.State;
        meta.County = string.IsNullOrWhiteSpace(meta.County) ? county ?? meta.County : meta.County;
        meta.ZipCode = string.IsNullOrWhiteSpace(meta.ZipCode) ? zip ?? meta.ZipCode : meta.ZipCode;
        meta.MortgageLender = string.IsNullOrWhiteSpace(meta.MortgageLender) ? lender ?? meta.MortgageLender : meta.MortgageLender;
        meta.LoanAmount = string.IsNullOrWhiteSpace(meta.LoanAmount) ? loanRaw ?? meta.LoanAmount : meta.LoanAmount;
        meta.Collaboration.Owner ??= agentUpn;
        profile.AgentNotes = string.Join("; ", new[] { profile.AgentNotes }.Concat(notes).Where(x => !string.IsNullOrWhiteSpace(x)));
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        profile.UpdatedUtc = now;

        if (!_db.AgentClients.Any(ac => ac.AgentUserId == agentOid && ac.ClientUserId == profile.ClientUserId))
        {
            _db.AgentClients.Add(new AgentClient
            {
                AgentUserId = agentOid,
                ClientUserId = profile.ClientUserId,
                AgentUpn = agentUpn,
                CreatedUtc = now
            });
        }
    }

    // =====================================================================
    // GET: /Clients/Create
    // =====================================================================
    [HttpGet]
    public IActionResult Create()
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var model = new CreateClientViewModel();

        var agentProfile = _db.AgentProfiles
            .AsNoTracking()
            .FirstOrDefault(x => x.AgentUserId == agentOid);

        if (agentProfile != null && !string.IsNullOrWhiteSpace(agentProfile.Npn))
            model.AgentNpn = agentProfile.Npn;
        if (agentProfile != null && !string.IsNullOrWhiteSpace(agentProfile.Phone))
            model.AgentPhone = agentProfile.Phone;

        return View(model);
    }

    // =====================================================================
    // POST: /Clients/Create
    // =====================================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateClientViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var agentUpn = GetAgentUpnForAudit();
        if (string.IsNullOrWhiteSpace(agentUpn))
            return Forbid();

        var firstName = (model.FirstName ?? "").Trim();
        var lastName = (model.LastName ?? "").Trim();
        var emailNorm = NormalizeEmail(model.Email);
        var phone = (model.Phone ?? "").Trim();
        var agentProfile = _db.AgentProfiles.FirstOrDefault(x => x.AgentUserId == agentOid);
        if (agentProfile == null)
        {
            agentProfile = new Domain.Entities.AgentProfile
            {
                AgentUserId = agentOid,
                AgentUpn = agentUpn,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(agentProfile);
            await _db.SaveChangesAsync();
        }
        var agentPhone = (agentProfile.Phone ?? "").Trim();
        var oneTimePassword = (model.OneTimePassword ?? "").Trim();
        var maritalStatus = (model.MaritalStatus ?? "").Trim();
        var recordType = NormalizeRecordType(model.RecordType);
        model.RecordType = recordType;
        var isPortalClient = IsPortalRecordType(recordType);
        var agentNpn = !string.IsNullOrWhiteSpace(agentProfile.Npn)
            ? agentProfile.Npn.Trim()
            : (model.AgentNpn ?? "").Trim();

        if (isPortalClient && string.IsNullOrWhiteSpace(firstName))
        {
            ModelState.AddModelError(nameof(CreateClientViewModel.FirstName), "First name is required.");
            return View(model);
        }

        if (isPortalClient && string.IsNullOrWhiteSpace(lastName))
        {
            ModelState.AddModelError(nameof(CreateClientViewModel.LastName), "Last name is required.");
            return View(model);
        }

        if (isPortalClient && string.IsNullOrWhiteSpace(emailNorm))
        {
            ModelState.AddModelError(nameof(CreateClientViewModel.Email), "Email is required.");
            return View(model);
        }

        // =========================
        // CRM fields (DB-backed) - defaults if UI doesn't send them
        // =========================
        var crmStatus = (model.CrmStatus ?? "").Trim();
        if (string.IsNullOrWhiteSpace(crmStatus)) crmStatus = "Lead";

        var crmPriority = (model.CrmPriority ?? "").Trim();
        if (string.IsNullOrWhiteSpace(crmPriority)) crmPriority = "Normal";

        var allowedStatus = new[] { "Lead", "Prospect", "Active", "Dormant" };
        if (!allowedStatus.Contains(crmStatus, StringComparer.OrdinalIgnoreCase))
            crmStatus = "Lead";

        var allowedPriority = new[] { "Low", "Normal", "High", "Urgent" };
        if (!allowedPriority.Contains(crmPriority, StringComparer.OrdinalIgnoreCase))
            crmPriority = "Normal";

        var crmTags = (model.CrmTags ?? "").Trim();
        crmTags = string.Join(", ",
            crmTags
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        );

        var crmLastTouch = model.CrmLastTouch;
        var crmNextDate = model.CrmNextDate;
        var crmNextText = (model.CrmNextText ?? "").Trim();
        var crmNotes = (model.CrmNotes ?? "").Trim(); // maps into AgentNotes
        var pipelineStage = NormalizePipelineStage(model.PipelineStage);
        if (isPortalClient)
        {
            pipelineStage = DefaultPipelineStageForRecordType(recordType);
            if (string.IsNullOrWhiteSpace(crmStatus) || crmStatus.Equals("Lead", StringComparison.OrdinalIgnoreCase) || crmStatus.Equals("Prospect", StringComparison.OrdinalIgnoreCase))
                crmStatus = "Active";
        }
        else if (pipelineStage is "Client" or "BusinessClient")
        {
            pipelineStage = ClientCrmMeta.DefaultPipelineStage;
        }

        // Keep "Next Action" coherent (if one is set, require both)
        var hasNextDate = crmNextDate.HasValue;
        var hasNextText = !string.IsNullOrWhiteSpace(crmNextText);
        if (hasNextDate && !hasNextText)
        {
            ModelState.AddModelError(nameof(CreateClientViewModel.CrmNextText),
                "Next Action text is required when Next Action date is set.");
            return View(model);
        }
        if (hasNextText && !hasNextDate)
        {
            ModelState.AddModelError(nameof(CreateClientViewModel.CrmNextDate),
                "Next Action date is required when Next Action text is set.");
            return View(model);
        }

        // =========================
        // Significant Other rules
        // =========================
        var needsSO =
            maritalStatus.Equals("Married", StringComparison.OrdinalIgnoreCase) ||
            maritalStatus.Equals("Domestic Partnership", StringComparison.OrdinalIgnoreCase);

        if (isPortalClient && needsSO)
        {
            if (string.IsNullOrWhiteSpace(model.SignificantOtherFirstName))
                ModelState.AddModelError(nameof(CreateClientViewModel.SignificantOtherFirstName), "Required for this status.");
            if (string.IsNullOrWhiteSpace(model.SignificantOtherLastName))
                ModelState.AddModelError(nameof(CreateClientViewModel.SignificantOtherLastName), "Required for this status.");
            if (model.SignificantOtherDOB == null)
                ModelState.AddModelError(nameof(CreateClientViewModel.SignificantOtherDOB), "Required for this status.");

            if (!ModelState.IsValid)
                return View(model);
        }

        string? clientObjectId = null;
        string? loginUpn = null;
        var createdGraphUser = false;

        try
        {
            // ==========================================================
            // HARD BLOCK: if email exists, DO NOT link to this agent
            // ==========================================================
            if (!string.IsNullOrWhiteSpace(emailNorm))
            {
                var existingByEmail = await _db.ClientProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.NormalizedEmail == emailNorm);

                if (existingByEmail != null)
                {
                    ModelState.AddModelError(nameof(CreateClientViewModel.Email),
                        "BLOCKED (409): That email is already tied to an existing client profile. " +
                        "For privacy and compliance, you cannot create or access that client. " +
                        "Create a NEW client account using a different email/login.");

                    Response.StatusCode = StatusCodes.Status409Conflict;
                    return View(model);
                }
            }

            if (isPortalClient)
            {
                var personalEmail = emailNorm
                    ?? throw new InvalidOperationException("Portal client email is required.");

                // ==========================================================
                // 1) Create Entra user (Graph provisioning)
                // ==========================================================
                (clientObjectId, loginUpn) = await _provisioning.CreateTenantUserAsync(
                    firstName,
                    lastName,
                    personalEmail,
                    oneTimePassword
                );

                clientObjectId = NormLower(clientObjectId);
                loginUpn = (loginUpn ?? "").Trim();

                if (string.IsNullOrWhiteSpace(clientObjectId))
                    throw new Exception("Provisioning returned an empty client user id.");

                if (string.IsNullOrWhiteSpace(loginUpn))
                    throw new Exception("Provisioning returned an empty login UPN.");

                createdGraphUser = true;
            }
            else
            {
                clientObjectId = CreateLeadClientUserId();
            }

            // ==========================================================
            // 1b) Upsert Agent Profile (NPN, display name)
            // ==========================================================
            var agentDisplayName = GetAgentDisplayName();
            var changed = false;

            if (!string.IsNullOrWhiteSpace(agentUpn) &&
                !string.Equals(agentProfile.AgentUpn, agentUpn, StringComparison.OrdinalIgnoreCase))
            {
                agentProfile.AgentUpn = agentUpn;
                changed = true;
            }

            var normalizedName = string.IsNullOrWhiteSpace(agentDisplayName)
                ? agentProfile.FullName
                : agentDisplayName;

            if (!string.IsNullOrWhiteSpace(normalizedName) &&
                !string.Equals(agentProfile.FullName, normalizedName, StringComparison.Ordinal))
            {
                agentProfile.FullName = normalizedName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(agentNpn) &&
                !string.Equals(agentProfile.Npn, agentNpn, StringComparison.OrdinalIgnoreCase))
            {
                agentProfile.Npn = agentNpn;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(agentPhone) &&
                !string.Equals(agentProfile.Phone, agentPhone, StringComparison.OrdinalIgnoreCase))
            {
                agentProfile.Phone = agentPhone;
                changed = true;
            }

            if (changed)
                agentProfile.UpdatedUtc = DateTime.UtcNow;

            // ==========================================================
            // 2) Save DB rows atomically
            // ==========================================================
            await using var tx = await _db.Database.BeginTransactionAsync();

            _db.ClientProfiles.Add(new ClientProfile
            {
                ClientUserId = clientObjectId,
                FirstName = firstName,
                LastName = lastName,
                Email = emailNorm ?? "",
                NormalizedEmail = emailNorm,
                Phone = phone,
                DOB = model.DOB,
                MaritalStatus = maritalStatus,

                // ✅ CRM (DB-backed)
                CrmStatus = crmStatus,
                CrmPriority = crmPriority,
                CrmLastTouch = crmLastTouch,
                CrmNextDate = crmNextDate,
                CrmNextText = crmNextText,
                CrmTags = crmTags,
                CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
                {
                    RecordType = recordType,
                    PipelineStage = pipelineStage,
                    Collaboration = new ClientCrmCollaboration
                    {
                        Owner = agentUpn
                    }
                }),

                // Relationship notes (DB)
                AgentNotes = crmNotes,

                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            // ✅ HARD PRIVACY: A client can only be linked to ONE agent
            _db.AgentClients.Add(new AgentClient
            {
                AgentUserId = agentOid,
                ClientUserId = clientObjectId,
                AgentUpn = agentUpn,
                CreatedUtc = DateTime.UtcNow
            });

            if (needsSO)
            {
                _db.HouseholdMembers.Add(new HouseholdMember
                {
                    ClientUserId = clientObjectId,
                    RelationshipType = "SignificantOther",
                    FirstName = (model.SignificantOtherFirstName ?? "").Trim(),
                    LastName = (model.SignificantOtherLastName ?? "").Trim(),
                    DOB = model.SignificantOtherDOB,
                    Email = (model.SignificantOtherEmail ?? "").Trim(),
                    Phone = (model.SignificantOtherPhone ?? "").Trim(),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            // ==========================================================
            // 3) Email should not rollback DB
            // ==========================================================
            try
            {
                if (isPortalClient)
                {
                    var clientPortalUrl = GetClientPortalBaseUrl();
if (string.IsNullOrWhiteSpace(emailNorm))
    throw new InvalidOperationException("Client email is required before sending the welcome email.");

await _provisioning.SendClientWelcomeEmailAsync(
    emailNorm,
    firstName,
    loginUpn ?? "",
    oneTimePassword,
    clientPortalUrl
);
                }
            }
            catch (Exception mailEx)
            {
                _logger.LogError(mailEx,
                    "Welcome email failed for Email={Email} ClientUserId={ClientUserId}",
                    emailNorm, clientObjectId);

                TempData["Created"] =
                    $"{RecordTypeLabel(recordType)} created. Login username: {loginUpn}. ⚠ Email failed to send: {mailEx.Message}";
                return RedirectToAction(nameof(Index));
            }

            TempData["Created"] = isPortalClient
                ? $"{RecordTypeLabel(recordType)} created. Login username: {loginUpn}"
                : $"Lead added to pipeline in {StageLabel(pipelineStage)}.";
            return RedirectToAction(nameof(Index));
        }
        catch (ODataError ex)
        {
            var msg = ex.Error?.Message ?? "Microsoft Graph request failed.";

            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                (msg.Contains("complexity", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("requirements", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("does not comply", StringComparison.OrdinalIgnoreCase)))
            {
                ModelState.AddModelError(nameof(CreateClientViewModel.OneTimePassword),
                    "Password rejected by Microsoft. Use 12+ characters and include 3 of 4: uppercase, lowercase, number, symbol. Avoid using the client’s name/email and common passwords.");

                if (createdGraphUser && !string.IsNullOrWhiteSpace(clientObjectId))
                {
                    try { await _provisioning.DeleteTenantUserAsync(clientObjectId); } catch { }
                }

                return View(model);
            }

            ModelState.AddModelError("", msg);

            if (createdGraphUser && !string.IsNullOrWhiteSpace(clientObjectId))
            {
                try { await _provisioning.DeleteTenantUserAsync(clientObjectId); } catch { }
            }

            return View(model);
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx,
                "DB conflict creating client. AgentOid={AgentOid} Email={Email} ClientObjectId={ClientObjectId}",
                agentOid, emailNorm, clientObjectId);

            ModelState.AddModelError(nameof(CreateClientViewModel.Email),
                "BLOCKED (409): That email conflicts with an existing client record. Create a new client using a different email/login.");

            Response.StatusCode = StatusCodes.Status409Conflict;

            if (createdGraphUser && !string.IsNullOrWhiteSpace(clientObjectId))
            {
                try { await _provisioning.DeleteTenantUserAsync(clientObjectId); } catch { }
            }

            return View(model);
        }
        catch (Exception ex)
        {
            var typeName = ex.GetType().Name ?? "";
            var isAuthFailed =
                typeName.Equals("AuthenticationFailedException", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ClientSecretCredential", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase);

            if (isAuthFailed)
            {
                ModelState.AddModelError("",
                    "Graph authentication failed (ClientSecretCredential). " +
                    "Fix AzureAd:TenantId / ClientId / ClientSecret and ensure admin consent for Graph permissions. " +
                    $"Details: {ex.Message}");
            }
            else
            {
                ModelState.AddModelError("", $"Failed to create client: {ex.Message}");
            }

            _logger.LogError(ex, "Failed to create client. AgentOid={AgentOid} Email={Email}", agentOid, emailNorm);

            if (createdGraphUser && !string.IsNullOrWhiteSpace(clientObjectId))
            {
                try { await _provisioning.DeleteTenantUserAsync(clientObjectId); } catch { }
            }

            return View(model);
        }
    }

    // =====================================================================
    // GET: /Clients/Edit/{clientUserId}
    // =====================================================================
    [HttpGet]
    public async Task<IActionResult> Edit(string clientUserId, string? returnUrl = null)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientUserIdNorm = NormLower(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserIdNorm))
            return RedirectToAction(nameof(Index));

        // Ownership check (ONLY creator/owner can edit)
        var linked = await _db.AgentClients.AnyAsync(x =>
            x.AgentUserId == agentOid &&
            x.ClientUserId == clientUserIdNorm);

        if (!linked)
            return Forbid();

        var profile = await _db.ClientProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientUserId == clientUserIdNorm);

        if (profile == null)
            return NotFound();

        var so = await _db.HouseholdMembers.AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.ClientUserId == clientUserIdNorm &&
                x.RelationshipType == "SignificantOther");
var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
meta.DocChecklist ??= new ClientCrmDocChecklist();
meta.OpportunityPlanning ??= new ClientCrmOpportunityPlanningChecklist();
meta.Collaboration ??= new ClientCrmCollaboration();
meta.Activities ??= new List<ClientCrmActivity>();
        var recordType = ResolveRecordType(profile.ClientUserId, meta);

        var vm = new EditClientViewModel
        {
            ClientUserId = profile.ClientUserId,
            RecordType = recordType,
            HasPortalAccess = HasPortalAccess(profile.ClientUserId),
            FirstName = profile.FirstName,
            LastName = profile.LastName,
            Email = profile.Email,
            Phone = profile.Phone,
            MaritalStatus = profile.MaritalStatus,
            DOB = profile.DOB,
            IsDobLocked = profile.DOB.HasValue,

            // ✅ CRM (DB-backed)
            CrmStatus = string.IsNullOrWhiteSpace(profile.CrmStatus) ? "Lead" : profile.CrmStatus,
            CrmPriority = string.IsNullOrWhiteSpace(profile.CrmPriority) ? "Normal" : profile.CrmPriority,
            CrmLastTouch = profile.CrmLastTouch,
            CrmNextDate = profile.CrmNextDate,
            CrmNextText = profile.CrmNextText,
            CrmTags = profile.CrmTags,
            CrmNotes = profile.AgentNotes,

            // Significant Other
            SignificantOtherFirstName = so?.FirstName,
            SignificantOtherLastName = so?.LastName,
            SignificantOtherDOB = so?.DOB,
            SignificantOtherEmail = so?.Email,
            SignificantOtherPhone = so?.Phone
        };

        var agentProfile = await _db.AgentProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentUserId == agentOid);

        if (agentProfile != null && !string.IsNullOrWhiteSpace(agentProfile.Npn))
            vm.AgentNpn = agentProfile.Npn;
        if (agentProfile != null && !string.IsNullOrWhiteSpace(agentProfile.Phone))
            vm.AgentPhone = agentProfile.Phone;

        PrepareEditView(vm, returnUrl);
        return View(vm);
    }

    // =====================================================================
    // POST: /Clients/Edit
    // =====================================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditClientViewModel model, string? returnUrl = null)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientUserIdNorm = NormLower(model.ClientUserId);
        if (string.IsNullOrWhiteSpace(clientUserIdNorm))
            return RedirectToAction(nameof(Index));

        // Ownership check (ONLY creator/owner can edit)
        var linked = await _db.AgentClients.AnyAsync(x =>
            x.AgentUserId == agentOid &&
            x.ClientUserId == clientUserIdNorm);

        if (!linked)
            return Forbid();

        var emailNorm = NormalizeEmail(model.Email);
        if (string.IsNullOrWhiteSpace(emailNorm))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email), "Email is required.");
            model.IsDobLocked = ProfileHasDob(clientUserIdNorm);
            model.HasPortalAccess = HasPortalAccess(clientUserIdNorm);
            PrepareEditView(model, returnUrl);
            return View(model);
        }

        // Prevent changing email to one that already exists for SOME OTHER client
        var emailCollision = await _db.ClientProfiles.AsNoTracking()
            .AnyAsync(x => x.NormalizedEmail == emailNorm && x.ClientUserId != clientUserIdNorm);

        if (emailCollision)
        {
            ModelState.AddModelError(nameof(EditClientViewModel.Email),
                "BLOCKED (409): That email is already used by another client. Choose a different email.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            model.IsDobLocked = ProfileHasDob(clientUserIdNorm);
            model.HasPortalAccess = HasPortalAccess(clientUserIdNorm);
            PrepareEditView(model, returnUrl);
            return View(model);
        }

        var profile = await _db.ClientProfiles
            .FirstOrDefaultAsync(x => x.ClientUserId == clientUserIdNorm);

        if (profile == null)
            return NotFound();

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        var existingRecordType = ResolveRecordType(profile.ClientUserId, meta);
        var hasPortalAccess = HasPortalAccess(profile.ClientUserId);
        var requestedRecordType = NormalizeRecordType(model.RecordType);
        model.RecordType = requestedRecordType;
        model.HasPortalAccess = hasPortalAccess;

        var agentProfile = _db.AgentProfiles.FirstOrDefault(x => x.AgentUserId == agentOid);
        if (agentProfile == null)
        {
            agentProfile = new Domain.Entities.AgentProfile
            {
                AgentUserId = agentOid,
                AgentUpn = GetAgentUpnForAudit(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.AgentProfiles.Add(agentProfile);
            await _db.SaveChangesAsync();
        }

        var agentNpn = agentProfile.Npn?.Trim();
        model.AgentNpn = agentNpn;

        var isPortalClient = hasPortalAccess || IsPortalRecordType(requestedRecordType);
        if (isPortalClient && string.IsNullOrWhiteSpace(agentNpn))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.AgentNpn),
                "Add your NPN in Manage Profile before editing portal clients.");
        }

        if (!hasPortalAccess && !string.Equals(requestedRecordType, "Lead", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.RecordType),
                "Use Quick View conversion to turn a lead into a client or business client.");
        }

        if (hasPortalAccess && string.Equals(requestedRecordType, "Lead", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(EditClientViewModel.RecordType),
                "Portal-enabled records cannot be changed back to Lead.");
        }

        if (!ModelState.IsValid)
        {
            model.IsDobLocked = profile.DOB.HasValue;
            PrepareEditView(model, returnUrl);
            return View(model);
        }

        // =========================
        // Core profile fields
        // =========================
        profile.FirstName = (model.FirstName ?? "").Trim();
        profile.LastName = (model.LastName ?? "").Trim();
        profile.Email = emailNorm ?? "";
        profile.NormalizedEmail = emailNorm;
        profile.Phone = (model.Phone ?? "").Trim();
        profile.MaritalStatus = (model.MaritalStatus ?? "").Trim();
        if (!profile.DOB.HasValue && model.DOB.HasValue)
            profile.DOB = model.DOB.Value.Date;
        model.IsDobLocked = profile.DOB.HasValue;

        meta.RecordType = hasPortalAccess ? requestedRecordType : "Lead";

        // =========================
        // CRM fields (DB-backed)
        // =========================
        var crmStatus = (model.CrmStatus ?? "").Trim();
        if (string.IsNullOrWhiteSpace(crmStatus)) crmStatus = profile.CrmStatus ?? "Lead";

        var crmPriority = (model.CrmPriority ?? "").Trim();
        if (string.IsNullOrWhiteSpace(crmPriority)) crmPriority = profile.CrmPriority ?? "Normal";

        var allowedStatus = new[] { "Lead", "Prospect", "Active", "Dormant" };
        if (!allowedStatus.Contains(crmStatus, StringComparer.OrdinalIgnoreCase))
            crmStatus = profile.CrmStatus ?? "Lead";

        var allowedPriority = new[] { "Low", "Normal", "High", "Urgent" };
        if (!allowedPriority.Contains(crmPriority, StringComparer.OrdinalIgnoreCase))
            crmPriority = profile.CrmPriority ?? "Normal";

        var crmTags = (model.CrmTags ?? "").Trim();
        crmTags = string.Join(", ",
            (crmTags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        );

        profile.CrmStatus = crmStatus;
        profile.CrmPriority = crmPriority;
        profile.CrmLastTouch = model.CrmLastTouch;
        profile.CrmNextDate = model.CrmNextDate;
        profile.CrmNextText = (model.CrmNextText ?? "").Trim();
        profile.CrmTags = crmTags;

        // Notes in DB (your UI label)
        profile.AgentNotes = (model.CrmNotes ?? "").Trim();

        // =========================
        // Upsert Agent Profile (NPN, display name)
        // =========================
        var agentUpn = GetAgentUpnForAudit();
        var agentDisplayName = GetAgentDisplayName();
        var changed = false;

        if (!string.IsNullOrWhiteSpace(agentUpn) &&
            !string.Equals(agentProfile.AgentUpn, agentUpn, StringComparison.OrdinalIgnoreCase))
        {
            agentProfile.AgentUpn = agentUpn;
            changed = true;
        }

        var normalizedName = string.IsNullOrWhiteSpace(agentDisplayName)
            ? agentProfile.FullName
            : agentDisplayName;

        if (!string.IsNullOrWhiteSpace(normalizedName) &&
            !string.Equals(agentProfile.FullName, normalizedName, StringComparison.Ordinal))
        {
            agentProfile.FullName = normalizedName;
            changed = true;
        }

        if (changed)
            agentProfile.UpdatedUtc = DateTime.UtcNow;

        if (!string.Equals(existingRecordType, meta.RecordType, StringComparison.OrdinalIgnoreCase))
        {
            var currentStage = NormalizePipelineStage(meta.PipelineStage);
            if (currentStage is "Client" or "BusinessClient")
            {
                meta.PipelineStage = DefaultPipelineStageForRecordType(meta.RecordType);
                meta.StageEnteredUtc = DateTime.UtcNow;
            }
        }

        if (hasPortalAccess && (string.IsNullOrWhiteSpace(profile.CrmStatus) ||
            profile.CrmStatus.Equals("Lead", StringComparison.OrdinalIgnoreCase) ||
            profile.CrmStatus.Equals("Prospect", StringComparison.OrdinalIgnoreCase)))
        {
            profile.CrmStatus = "Active";
        }

        profile.UpdatedUtc = DateTime.UtcNow;

        // =========================
        // Significant Other rules
        // =========================
        var maritalStatus = (model.MaritalStatus ?? "").Trim();
        var needsSO =
            maritalStatus.Equals("Married", StringComparison.OrdinalIgnoreCase) ||
            maritalStatus.Equals("Domestic Partnership", StringComparison.OrdinalIgnoreCase);

        if (needsSO)
        {
            if (string.IsNullOrWhiteSpace(model.SignificantOtherFirstName))
                ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherFirstName), "Required for this status.");
            if (string.IsNullOrWhiteSpace(model.SignificantOtherLastName))
                ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherLastName), "Required for this status.");
            if (model.SignificantOtherDOB == null)
                ModelState.AddModelError(nameof(EditClientViewModel.SignificantOtherDOB), "Required for this status.");

            if (!ModelState.IsValid)
            {
                PrepareEditView(model, returnUrl);
                return View(model);
            }

            var so = await _db.HouseholdMembers
                .FirstOrDefaultAsync(x =>
                    x.ClientUserId == clientUserIdNorm &&
                    x.RelationshipType == "SignificantOther");

            if (so == null)
            {
                so = new HouseholdMember
                {
                    ClientUserId = clientUserIdNorm,
                    RelationshipType = "SignificantOther",
                    CreatedUtc = DateTime.UtcNow
                };
                _db.HouseholdMembers.Add(so);
            }

            so.FirstName = (model.SignificantOtherFirstName ?? "").Trim();
            so.LastName = (model.SignificantOtherLastName ?? "").Trim();
            so.DOB = model.SignificantOtherDOB;
            so.Email = (model.SignificantOtherEmail ?? "").Trim();
            so.Phone = (model.SignificantOtherPhone ?? "").Trim();
            so.UpdatedUtc = DateTime.UtcNow;

            // Mirror (helps fallback display logic)
            profile.SignificantOtherFirstName = so.FirstName;
            profile.SignificantOtherLastName = so.LastName;
            profile.SignificantOtherDOB = so.DOB;
            profile.SignificantOtherEmail = so.Email;
            profile.SignificantOtherPhone = so.Phone;
        }
        else
        {
            profile.SignificantOtherFirstName = null;
            profile.SignificantOtherLastName = null;
            profile.SignificantOtherDOB = null;
            profile.SignificantOtherEmail = null;
            profile.SignificantOtherPhone = null;

            var so = await _db.HouseholdMembers
                .FirstOrDefaultAsync(x =>
                    x.ClientUserId == clientUserIdNorm &&
                    x.RelationshipType == "SignificantOther");

            if (so != null)
                _db.HouseholdMembers.Remove(so);
        }

        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);

        await _db.SaveChangesAsync();

        TempData["Created"] = $"{RecordTypeLabel(meta.RecordType)} profile updated.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    private bool ProfileHasDob(string clientUserIdNorm) => _db.ClientProfiles
        .AsNoTracking()
        .Any(x => x.ClientUserId == clientUserIdNorm && x.DOB.HasValue);

    public sealed class OpportunityPlanningRequest
    {
        public bool LifeInsurance { get; set; }
        public bool DisabilityIncome { get; set; }
        public bool LongTermCare { get; set; }
        public bool CriticalIllness { get; set; }
        public bool TerminalIllness { get; set; }
        public bool AnnuityRetirement { get; set; }
        public bool MortgageProtection { get; set; }
        public bool FinalExpense { get; set; }
        public bool Medicare { get; set; }
        public bool Health { get; set; }
        public bool DentalVision { get; set; }
        public bool HospitalIndemnity { get; set; }
        public bool PersonalAuto { get; set; }
        public bool HomeRenters { get; set; }
        public bool UmbrellaLiability { get; set; }
        public bool FloodEarthquake { get; set; }
        public bool CommercialAuto { get; set; }
        public bool GeneralLiability { get; set; }
        public bool BusinessOwnersPolicy { get; set; }
        public bool WorkersComp { get; set; }
        public bool KeyPersonBuySell { get; set; }
        public bool GroupBenefits { get; set; }
    }

    public sealed class QuickViewRequest
    {
        public string ClientUserId { get; set; } = "";
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Phone2 { get; set; }
        public DateTime? Dob { get; set; }
        public string? Gender { get; set; }
        public string? AddressLine { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? County { get; set; }
        public string? ZipCode { get; set; }
        public string? Age { get; set; }
        public string? Btc { get; set; }
        public string? MortgageLender { get; set; }
        public string? LoanAmount { get; set; }
        public string CrmStatus { get; set; } = "Lead";
        public string CrmPriority { get; set; } = "Normal";
        public DateTime? CrmLastTouch { get; set; }
        public DateTime? CrmNextDate { get; set; }
        public string? CrmNextText { get; set; }
        public string? CrmTags { get; set; }
        public string? AgentNotes { get; set; }
        public string? RecordType { get; set; }
        public string? PipelineStage { get; set; }
        public double? PipelineOrder { get; set; }
        public string? MeetingLocation { get; set; }
        public string? ZoomJoinUrl { get; set; }
        public bool UsePersonalZoomLink { get; set; }
        public string? MeetingTime { get; set; }
        public int MeetingDurationMinutes { get; set; } = 30;
        public string? WaitingOn { get; set; }
        public string? PinnedBrief { get; set; }
        public bool DocIdReceived { get; set; }
        public bool DocAppSent { get; set; }
        public bool DocAppSigned { get; set; }
        public bool DocPolicyDelivered { get; set; }
        public bool DocReviewBooked { get; set; }
        public OpportunityPlanningRequest? OpportunityPlanning { get; set; }
        public string? AssignedOwner { get; set; }
        public string? Watchers { get; set; }
        public string? MentionNote { get; set; }
    }

    public sealed class SaveAdvancedMarketsInputsRequest
    {
        public Guid? ClientProfileId { get; set; }
        public string ClientUserId { get; set; } = "";
        public AdvancedMarketsPageViewModel? Inputs { get; set; }
    }

    public sealed class SaveFinancialPlanRequest
    {
        public Guid? ClientProfileId { get; set; }
        public string ClientUserId { get; set; } = "";
        public string JsonData { get; set; } = "{}";
        public int? Version { get; set; }
    }

    public sealed class DistributionPlanCanonicalInput
    {
        public string SchemaVersion { get; set; } = "1.0";
        public int PlanVersion { get; set; } = 1;
        public double RetireAge { get; set; }
        public double EndAge { get; set; }
        public double InflationPct { get; set; }
        public double RetirementBase { get; set; }
        public double DesiredIncome { get; set; }
        public double GuaranteedIncome { get; set; }
        public double EmergencyReserve { get; set; }
        public bool ManualBaseOverride { get; set; }
        public double InvAllocPct { get; set; }
        public double InvReturnPct { get; set; }
        public double InvTaxPct { get; set; }
        public double LiAllocPct { get; set; }
        public double LiReturnPct { get; set; }
        public double LiTaxPct { get; set; }
        public string LiAccessMode { get; set; } = "withdrawal";
        public string LiPolicyType { get; set; } = "whole";
        public double AnnAllocPct { get; set; }
        public double AnnReturnPct { get; set; }
        public double AnnTaxPct { get; set; }
        public string AnnDesign { get; set; } = "fixed";
        public List<string> WithdrawalOrder { get; set; } = new List<string> { "inv", "li", "ann", "reserve" };
    }

    private static string? ValidateDistributionCanonical(JsonObject canonical)
    {
        double GetD(string name, double def = 0)
        {
            if (canonical[name] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
            return def;
        }
        bool InRange(double v, double min, double max) => v >= min && v <= max;
        var retireAge = GetD("retireAge");
        var endAge = GetD("endAge");
        if (retireAge <= 0) return "retireAge must be > 0";
        if (endAge <= retireAge) return "endAge must be greater than retireAge";
        var retirementBase = GetD("retirementBase");
        if (retirementBase < 0) return "retirementBase must be >= 0";
        if (GetD("desiredIncome") < 0) return "desiredIncome must be >= 0";
        if (GetD("guaranteedIncome") < 0) return "guaranteedIncome must be >= 0";
        if (GetD("emergencyReserve") < 0) return "emergencyReserve must be >= 0";

        double inv = GetD("invAllocPct"), li = GetD("liAllocPct"), ann = GetD("annAllocPct");
        if (!InRange(inv,0,100) || !InRange(li,0,100) || !InRange(ann,0,100))
            return "Allocation percents must be between 0 and 100";
        if (Math.Abs(inv + li + ann - 100) > 0.001)
            return "Allocation percents must total 100%";

        double rtnMin=-50, rtnMax=20;
        if (!InRange(GetD("invReturnPct"), rtnMin, rtnMax)) return "invReturnPct out of range";
        if (!InRange(GetD("liReturnPct"), rtnMin, rtnMax)) return "liReturnPct out of range";
        if (!InRange(GetD("annReturnPct"), rtnMin, rtnMax)) return "annReturnPct out of range";
        double taxMin=0, taxMax=100;
        if (!InRange(GetD("invTaxPct"), taxMin, taxMax)) return "invTaxPct out of range";
        if (!InRange(GetD("liTaxPct"), taxMin, taxMax)) return "liTaxPct out of range";
        if (!InRange(GetD("annTaxPct"), taxMin, taxMax)) return "annTaxPct out of range";
        return null;
    }

    public sealed class OutcomeRequest
    {
        public string ClientUserId { get; set; } = "";
        public string OutcomeCode { get; set; } = "";
        public string? CustomNote { get; set; }
        public string? MeetingLocation { get; set; }
        public string? ZoomJoinUrl { get; set; }
        public bool UsePersonalZoomLink { get; set; }
        public string? MeetingTime { get; set; }
        public int MeetingDurationMinutes { get; set; } = 30;
    }

    public sealed class BulkUpdateRequest
    {
        public List<string> ClientUserIds { get; set; } = new();
        public string? PipelineStage { get; set; }
        public DateTime? CrmNextDate { get; set; }
        public string? CrmNextText { get; set; }
        public string? CrmPriority { get; set; }
        public string? CrmTags { get; set; }
        public string? SharedNote { get; set; }
        public string? WaitingOn { get; set; }
    }

    public sealed class ActivityRequest
    {
        public string ClientUserId { get; set; } = "";
        public string Type { get; set; } = "Note";
        public string Date { get; set; } = "";
        public string Note { get; set; } = "";
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public string? CalendarEventId { get; set; }
        public string? CalendarWebLink { get; set; }
    }

    public sealed class ClientRequest
    {
        public string ClientUserId { get; set; } = "";
    }

    public sealed class EnablePortalAccessRequest
    {
        public string ClientUserId { get; set; } = "";
        public string? RecordType { get; set; }
    }

    public sealed class QueueUpdateRequest
    {
        public string Queue { get; set; } = "";
        public string ClientUserId { get; set; } = "";
        public DateTime? CrmNextDate { get; set; }
        public string? CrmNextText { get; set; }
        public string? CrmPriority { get; set; }
        public string? WaitingOn { get; set; }
        public string? QueueNote { get; set; }
        public string? MeetingLocation { get; set; }
        public string? ZoomJoinUrl { get; set; }
        public bool UsePersonalZoomLink { get; set; }
        public string? MeetingTime { get; set; }
        public int MeetingDurationMinutes { get; set; } = 30;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QueueUpdate(QueueUpdateRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);
        if (profile == null)
            return Forbid();

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        var nextDate = request.CrmNextDate?.Date;
        var nextText = Norm(request.CrmNextText);
        var priority = Norm(request.CrmPriority);
        var waitingOn = NormalizeWaitingOn(request.WaitingOn);
        var queueNote = Norm(request.QueueNote);
        var meetingLocation = Norm(request.MeetingLocation);
        var zoomJoinUrl = Norm(request.ZoomJoinUrl);
        var meetingTime = Norm(request.MeetingTime);
        var meetingDurationMinutes = request.MeetingDurationMinutes <= 0 ? 30 : request.MeetingDurationMinutes;
        var allowedPriority = new[] { "Low", "Normal", "High", "Urgent" };

        profile.CrmNextDate = nextDate;
        profile.CrmNextText = string.IsNullOrWhiteSpace(nextText) ? null : nextText;
        profile.CrmPriority = allowedPriority.Contains(priority) ? priority : (string.IsNullOrWhiteSpace(profile.CrmPriority) ? "Normal" : profile.CrmPriority);
        profile.UpdatedUtc = DateTime.UtcNow;

        meta.WaitingOn = waitingOn;
        meta.MeetingLocation = string.IsNullOrWhiteSpace(meetingLocation) ? null : meetingLocation;
        meta.ZoomJoinUrl = string.IsNullOrWhiteSpace(zoomJoinUrl) ? null : zoomJoinUrl;
        meta.UsePersonalZoomLink = request.UsePersonalZoomLink;
        meta.MeetingTime = string.IsNullOrWhiteSpace(meetingTime) ? "09:00" : meetingTime;
        meta.MeetingDurationMinutes = meetingDurationMinutes;

        if (!string.IsNullOrWhiteSpace(queueNote))
        {
            meta.Activities.Add(new ClientCrmActivity
            {
                Type = "Note",
                Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                Note = queueNote,
                IsSystem = false,
                CreatedBy = GetAgentUpnForAudit(),
                CreatedUtc = DateTime.UtcNow
            });
            profile.CrmLastTouch = DateTime.UtcNow.Date;
            meta.LastContactChannel = "Queue";
        }

        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);

        await _db.SaveChangesAsync();

        TempData["Created"] = "Queue item updated.";
        return RedirectToAction(nameof(Queue), new { queue = request.Queue });
    }

    [HttpGet]
    public async Task<IActionResult> QuickView(string clientUserId)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientUserIdNorm = NormLower(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserIdNorm))
        {
            _logger.LogWarning("QuickView BAD REQUEST agent={Agent} clientUserId(empty)", agentOid);
            return BadRequest("clientUserId is required.");
        }

        try
        {
            var profile = await GetOwnedClientProfileAsync(agentOid, clientUserIdNorm);
            if (profile == null)
            {
                _logger.LogWarning("QuickView FORBID agent={Agent} clientUserId={ClientUserId}", agentOid, clientUserIdNorm);
                return Forbid();
            }

            var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
            _logger.LogInformation("QuickView OK agent={Agent} clientUserId={ClientUserId} profileId={ProfileId}", agentOid, clientUserIdNorm, profile.Id);

            var nowUtc = DateTime.UtcNow;
            var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
            return Json(BuildQuickViewPayload(profile, meta, dialTimeZone, nowUtc));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuickView ERROR agent={Agent} clientUserId={ClientUserId}", agentOid, clientUserIdNorm);
            return StatusCode(500, $"QuickView failed: {ex.Message}");
        }
    }

    [HttpGet]
    public async Task<IActionResult> AdvancedMarketsBusinessClients(string? q)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var search = NormLower(q);
        var ownedProfiles = await (
            from link in _db.AgentClients.AsNoTracking()
            join profile in _db.ClientProfiles.AsNoTracking() on link.ClientUserId equals profile.ClientUserId
            where link.AgentUserId == agentOid
            select profile
        ).ToListAsync();

        var businessProfiles = new List<(Guid Id, string ClientUserId, string DisplayName, string Email, string Phone, DateTime UpdatedUtc)>();
        foreach (var profile in ownedProfiles)
        {
            var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
            var recordType = ResolveRecordType(profile.ClientUserId, meta);
            if (!IsBusinessClientRecordType(recordType))
                continue;

            var displayName = $"{Norm(profile.FirstName)} {Norm(profile.LastName)}".Trim();
            var haystack = string.Join(" ",
                displayName,
                profile.Email ?? "",
                profile.Phone ?? "",
                meta.City ?? "",
                meta.State ?? "").ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(search) && !haystack.Contains(search))
                continue;

            businessProfiles.Add((
                profile.Id,
                profile.ClientUserId,
                string.IsNullOrWhiteSpace(displayName) ? "Business Client" : displayName,
                profile.Email ?? "",
                profile.Phone ?? "",
                profile.UpdatedUtc
            ));
        }

        var profileIds = businessProfiles.Select(x => x.Id).ToList();
        var savedStateIds = profileIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.FinanceToolStates
                .AsNoTracking()
                .Where(x => x.ToolId == AdvancedMarketsToolId && profileIds.Contains(x.ClientProfileId))
                .Select(x => x.ClientProfileId)
                .ToListAsync())
                .ToHashSet();

        var results = businessProfiles
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenBy(x => x.DisplayName)
            .Take(string.IsNullOrWhiteSpace(search) ? 8 : 16)
            .Select(x => new
            {
                clientUserId = x.ClientUserId,
                clientProfileId = x.Id,
                displayName = x.DisplayName,
                email = x.Email,
                phone = x.Phone,
                hasSavedInputs = savedStateIds.Contains(x.Id)
            });

        return Json(results);
    }

    [HttpGet]
    public async Task<IActionResult> FinancialPlanClients(string? q)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        try
        {
            var search = NormLower(q);

            // Pull owned profiles first (no projection logic inside EF to avoid translation surprises)
            var ownedProfiles = await (
                from link in _db.AgentClients.AsNoTracking()
                join profile in _db.ClientProfiles.AsNoTracking() on link.ClientUserId equals profile.ClientUserId
                where link.AgentUserId == agentOid
                select new {
                    profile.Id,
                    profile.ClientUserId,
                    profile.FirstName,
                    profile.LastName,
                    profile.Email,
                    profile.Phone,
                    profile.UpdatedUtc
                }
            ).ToListAsync();

            var profileIds = ownedProfiles.Select(x => x.Id).ToList();
            var savedPlanIds = profileIds.Count == 0
                ? new HashSet<Guid>()
                : (await _db.ClientFinancialPlans
                    .AsNoTracking()
                    .Where(x => profileIds.Contains(x.ClientId) && !x.IsDeleted)
                    .Select(x => x.ClientId)
                    .ToListAsync()).ToHashSet();

            // Build display + haystack after materialization (null-safe)
            var results = ownedProfiles
                .Select(p =>
                {
                    var first = Norm(p.FirstName);
                    var last = Norm(p.LastName);
                    var displayName = $"{first} {last}".Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = "Client";
                    var haystack = string.Join(" ", displayName, p.Email ?? "", p.Phone ?? "").ToLowerInvariant();
                    return new
                    {
                        p.ClientUserId,
                        p.Id,
                        displayName,
                        email = p.Email ?? "",
                        phone = p.Phone ?? "",
                        haystack
                    };
                })
                .Where(x => string.IsNullOrWhiteSpace(search) || x.haystack.Contains(search))
                .OrderByDescending(x => x.Id == Guid.Empty ? DateTime.MinValue : DateTime.MaxValue) // deterministic even if UpdatedUtc null
                .ThenBy(x => x.displayName)
                .Take(string.IsNullOrWhiteSpace(search) ? 12 : 24)
                .Select(x => new
                {
                    clientUserId = x.ClientUserId,
                    clientProfileId = x.Id,
                    displayName = x.displayName,
                    email = x.email,
                    phone = x.phone,
                    hasSavedPlan = savedPlanIds.Contains(x.Id)
                })
                .ToList();

            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinancialPlanClients error agent={Agent} q={Search}", agentOid, q);
            // Fail-soft for search: return empty array so UI does not break
            return Json(Array.Empty<object>());
        }
    }

    [HttpGet]
    public async Task<IActionResult> AdvancedMarketsInputs(string? clientUserId, Guid? clientProfileId = null)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        ClientProfile? profile = null;
        if (clientProfileId.HasValue && clientProfileId.Value != Guid.Empty)
            profile = await GetOwnedClientProfileAsync(agentOid, clientProfileId.Value);

        if (profile == null && !string.IsNullOrWhiteSpace(clientUserId))
            profile = await GetOwnedClientProfileAsync(agentOid, clientUserId);

        if (profile == null) return Forbid();

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        var recordType = ResolveRecordType(profile.ClientUserId, meta);
        var row = await _db.FinanceToolStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id && x.ToolId == AdvancedMarketsToolId);

        // Allow load if explicitly a business client OR if a state row already exists (grandfathered data)
        if (!IsBusinessClientRecordType(recordType) && row == null)
            return BadRequest("Advanced Markets inputs are only available for business clients.");

        var fallback = BuildDefaultAdvancedMarketsInputs(profile, meta);
        var inputs = NormalizeAdvancedMarketsInputs(DeserializeAdvancedMarketsInputs(row?.JsonState), fallback);
        var fingerprint = row?.JsonState != null ? FingerprintPayload(row.JsonState) : "(none)";
        var clientName = $"{Norm(profile.FirstName)} {Norm(profile.LastName)}".Trim();

        _logger.LogInformation("AdvancedMarketsInputs GET clientUserId={ClientUserId} profileId={ProfileId} hasRow={HasRow} rowId={RowId} rowLen={RowLen} fp={Fingerprint}",
            profile.ClientUserId,
            profile.Id,
            row != null,
            row?.Id,
            row?.JsonState?.Length ?? 0,
            fingerprint);

        return Json(new
        {
            clientUserId = profile.ClientUserId,
            clientProfileId = profile.Id,
            clientName = string.IsNullOrWhiteSpace(clientName) ? "Business Client" : clientName,
            hasSavedInputs = row != null,
            updatedUtc = row?.UpdatedUtc,
            inputs,
            fingerprint
        });
    }

    [HttpGet("/clients/{id}/financial-plan")]
    public async Task<IActionResult> FinancialPlan(Guid id, string? clientUserId = null)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientUserIdNorm = NormLower(clientUserId);
        _logger.LogInformation("FinancialPlan GET start agent={Agent} routeId={RouteId} clientUserId={ClientUserId}", agentOid, id, clientUserIdNorm);

        try
        {
            ClientProfile? profile = await GetOwnedClientProfileAsync(agentOid, id);
            if (profile == null && !string.IsNullOrWhiteSpace(clientUserIdNorm))
                profile = await GetOwnedClientProfileAsync(agentOid, clientUserIdNorm);

            if (profile == null) return Forbid();

            var row = await _db.ClientFinancialPlans.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientId == profile.Id && !x.IsDeleted);

            var json = row?.JsonData;
            if (string.IsNullOrWhiteSpace(json)) json = "{}";
            json = SanitizeFinancialPlanJson(json); // defensive: strip derived/deprecated fields before serving

            string fingerprint = "(empty)";
            try { fingerprint = FingerprintPayload(json); } catch { fingerprint = "(error)"; }

            var displayName = $"{Norm(profile.FirstName)} {Norm(profile.LastName)}".Trim();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "Client";

            _logger.LogInformation("FinancialPlan GET ok agent={Agent} clientUserId={ClientUserId} profileId={ProfileId} hasRow={HasRow} rowId={RowId} len={Len}",
                agentOid, profile.ClientUserId, profile.Id, row != null, row?.Id, json.Length);

            return Json(new
            {
                clientUserId = profile.ClientUserId,
                clientProfileId = profile.Id,
                clientName = displayName,
                hasPlan = row != null,
                jsonData = json,
                version = row?.Version ?? 1,
                updatedUtc = row?.LastUpdatedUtc,
                updatedBy = row?.UpdatedBy,
                fingerprint
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinancialPlan GET error agent={Agent} routeId={RouteId} clientUserId={ClientUserId}", agentOid, id, clientUserId);
            // Fail-soft for hydration: return an empty plan so UI can continue
            return Json(new
            {
                clientUserId = clientUserIdNorm,
                clientProfileId = (Guid?)null,
                clientName = "Client",
                hasPlan = false,
                jsonData = "{}",
                version = 1,
                updatedUtc = (DateTime?)null,
                updatedBy = "",
                fingerprint = "(error)"
            });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SaveAdvancedMarketsInputs([FromBody] SaveAdvancedMarketsInputsRequest? request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        if (request == null)
        {
            _logger.LogWarning("AdvancedMarkets SAVE null request");
            return AdvancedMarketsValidationError("Invalid Advanced Markets inputs payload.");
        }

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("AdvancedMarkets SAVE modelstate invalid for clientUserId={ClientUserId} errors={Errors}",
                request.ClientUserId,
                string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
            return AdvancedMarketsValidationError("Some Advanced Markets inputs need attention before saving.");
        }

        if ((!request.ClientProfileId.HasValue || request.ClientProfileId.Value == Guid.Empty)
            && string.IsNullOrWhiteSpace(request.ClientUserId))
        {
            _logger.LogWarning("AdvancedMarkets SAVE missing managed client identifier");
            return BadRequest("A business client is required.");
        }

        if (request.Inputs == null)
        {
            _logger.LogWarning("AdvancedMarkets SAVE missing inputs for clientUserId={ClientUserId}", request.ClientUserId);
            return AdvancedMarketsValidationError("Advanced Markets inputs are required.");
        }

        ClientProfile? profile = null;
        if (request.ClientProfileId.HasValue && request.ClientProfileId.Value != Guid.Empty)
            profile = await GetOwnedClientProfileAsync(agentOid, request.ClientProfileId.Value);

        if (profile == null && !string.IsNullOrWhiteSpace(request.ClientUserId))
            profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);

        if (profile == null) return Forbid();

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        var recordType = ResolveRecordType(profile.ClientUserId, meta);
        var existingRow = await _db.FinanceToolStates
            .FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id && x.ToolId == AdvancedMarketsToolId);

        // Allow save if explicitly a business client OR if a state row already exists (grandfathered data)
        if (!IsBusinessClientRecordType(recordType) && existingRow == null)
            return BadRequest("Advanced Markets inputs are only available for business clients.");

        var requestFingerprint = FingerprintPayload(JsonSerializer.Serialize(request.Inputs, AdvancedMarketsStateJsonOptions));
        _logger.LogInformation("AdvancedMarketsInputs SAVE start clientUserId={ClientUserId} profileId={ProfileId} requestFp={RequestFp}",
            request.ClientUserId, profile.Id, requestFingerprint);

        var normalizedInputs = NormalizeAdvancedMarketsInputs(request.Inputs ?? new AdvancedMarketsPageViewModel(), BuildDefaultAdvancedMarketsInputs(profile, meta));
        var row = existingRow
            ?? await _db.FinanceToolStates
                .FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id && x.ToolId == AdvancedMarketsToolId);

        var nowUtc = DateTime.UtcNow;
        var jsonState = JsonSerializer.Serialize(normalizedInputs, AdvancedMarketsStateJsonOptions);
        var fingerprint = FingerprintPayload(jsonState);
        if (row == null)
        {
            row = new FinanceToolState
            {
                ClientProfileId = profile.Id,
                ToolId = AdvancedMarketsToolId,
                JsonState = jsonState,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };
            _db.FinanceToolStates.Add(row);
            _logger.LogInformation("AdvancedMarketsInputs SAVE create clientUserId={ClientUserId} profileId={ProfileId} rowId={RowId} jsonLen={JsonLen} fp={Fingerprint}",
                profile.ClientUserId, profile.Id, row.Id, jsonState.Length, fingerprint);
        }
        else
        {
            row.JsonState = jsonState;
            row.UpdatedUtc = nowUtc;
            _logger.LogInformation("AdvancedMarketsInputs SAVE update clientUserId={ClientUserId} profileId={ProfileId} rowId={RowId} jsonLen={JsonLen} fp={Fingerprint}",
                profile.ClientUserId, profile.Id, row.Id, jsonState.Length, fingerprint);
        }

        await _db.SaveChangesAsync();

        // verify by reloading from DB
        var verifyRow = await _db.FinanceToolStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientProfileId == profile.Id && x.ToolId == AdvancedMarketsToolId);
        var verifyFingerprint = verifyRow?.JsonState != null ? FingerprintPayload(verifyRow.JsonState) : "(none)";

        if (verifyRow == null || verifyRow.JsonState?.Length == 0 || verifyFingerprint != fingerprint)
        {
            _logger.LogError("AdvancedMarkets SAVE verification failed clientUserId={ClientUserId} profileId={ProfileId} rowId={RowId} savedFp={SavedFp} verifyFp={VerifyFp}",
                profile.ClientUserId, profile.Id, verifyRow?.Id, fingerprint, verifyFingerprint);
            return StatusCode(500, "Advanced Markets save verification failed.");
        }

        return Json(new
        {
            ok = true,
            clientUserId = profile.ClientUserId,
            clientProfileId = profile.Id,
            updatedUtc = row.UpdatedUtc,
            inputs = normalizedInputs,
            fingerprint
        });
    }

    [HttpPost("/clients/{id}/financial-plan")]
    public async Task<IActionResult> SaveFinancialPlan(Guid id, [FromBody] SaveFinancialPlanRequest? request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        _logger.LogInformation("FinancialPlan SAVE start agent={Agent} routeId={RouteId} clientUserId={ClientUserId}", agentOid, id, request?.ClientUserId);

        if (request == null)
        {
            _logger.LogWarning("FinancialPlan SAVE null request profileId={ProfileId}", id);
            return BadRequest("Invalid financial plan payload.");
        }

        if (string.IsNullOrWhiteSpace(request.JsonData))
        {
            _logger.LogWarning("FinancialPlan SAVE empty json profileId={ProfileId}", id);
            return BadRequest("JsonData is required.");
        }

        try
        {
            ClientProfile? profile = await GetOwnedClientProfileAsync(agentOid, id);
            if (profile == null && request.ClientProfileId.HasValue && request.ClientProfileId.Value != Guid.Empty)
                profile = await GetOwnedClientProfileAsync(agentOid, request.ClientProfileId.Value);
            if (profile == null && !string.IsNullOrWhiteSpace(request.ClientUserId))
                profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);

            if (profile == null) return Forbid();

            var nowUtc = DateTime.UtcNow;
            var incomingJson = string.IsNullOrWhiteSpace(request.JsonData) ? "{}" : request.JsonData;
            // Include deleted rows so we can revive instead of violating unique index
            var row = await _db.ClientFinancialPlans.FirstOrDefaultAsync(x => x.ClientId == profile.Id);
            // Validate canonical distribution planner payload if present
            try
            {
                var root = JsonNode.Parse(incomingJson) as JsonObject ?? new JsonObject();
                var dist = root["distribution"] as JsonObject;
                var canonical = dist?["canonicalInput"] as JsonObject;
                if (canonical != null)
                {
                    var err = ValidateDistributionCanonical(canonical);
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        _logger.LogWarning("FinancialPlan SAVE validation failed profileId={ProfileId} error={Error}", id, err);
                        return BadRequest(err);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FinancialPlan SAVE validation parse error profileId={ProfileId}", id);
                return BadRequest("Invalid financial plan payload.");
            }

            var sanitized = SanitizeFinancialPlanJson(incomingJson, row?.JsonData);
            if (row == null)
            {
                row = new ClientFinancialPlan
                {
                    ClientId = profile.Id,
                    JsonData = sanitized,
                    LastUpdatedUtc = nowUtc,
                    UpdatedBy = GetAgentUpnForAudit(),
                    Version = request.Version ?? 1,
                    IsDeleted = false
                };
                _db.ClientFinancialPlans.Add(row);
                _logger.LogInformation("FinancialPlan SAVE create clientUserId={ClientUserId} profileId={ProfileId} rowId={RowId} len={Len}",
                    profile.ClientUserId, profile.Id, row.Id, sanitized.Length);
            }
            else
            {
                if (row.IsDeleted) row.IsDeleted = false;
                row.JsonData = sanitized;
                row.LastUpdatedUtc = nowUtc;
                row.UpdatedBy = GetAgentUpnForAudit();
                row.Version = (request.Version ?? row.Version) + 1;
                _logger.LogInformation("FinancialPlan SAVE update clientUserId={ClientUserId} profileId={ProfileId} rowId={RowId} len={Len} version={Version}",
                    profile.ClientUserId, profile.Id, row.Id, sanitized.Length, row.Version);
            }

            await _db.SaveChangesAsync();

            var verify = await _db.ClientFinancialPlans.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientId == profile.Id && !x.IsDeleted);
            if (verify == null || string.IsNullOrWhiteSpace(verify.JsonData))
            {
                _logger.LogError("FinancialPlan SAVE verification failed clientUserId={ClientUserId} profileId={ProfileId}", profile.ClientUserId, profile.Id);
                return StatusCode(500, "Financial plan save verification failed.");
            }

            return Json(new
            {
                ok = true,
                clientUserId = profile.ClientUserId,
                clientProfileId = profile.Id,
                updatedUtc = verify.LastUpdatedUtc,
                version = verify.Version,
                jsonData = verify.JsonData,
                fingerprint = FingerprintPayload(verify.JsonData)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinancialPlan SAVE error agent={Agent} routeId={RouteId} clientUserId={ClientUserId}", agentOid, id, request.ClientUserId);
            return StatusCode(500, "Financial plan save failed.");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnablePortalAccess([FromBody] EnablePortalAccessRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var oldClientUserId = NormLower(request.ClientUserId);
        if (string.IsNullOrWhiteSpace(oldClientUserId))
            return BadRequest("Client id is required.");

        var profile = await GetOwnedClientProfileAsync(agentOid, oldClientUserId);
        if (profile == null) return Forbid();

        if (HasPortalAccess(profile.ClientUserId))
            return BadRequest("Portal access is already enabled for this record.");

        var emailNorm = NormalizeEmail(profile.Email);
        if (string.IsNullOrWhiteSpace(emailNorm))
            return BadRequest("An email is required before sending portal access.");

        var recordType = NormalizeRecordType(request.RecordType);
        if (!IsPortalRecordType(recordType))
            recordType = "Client";

        var emailCollision = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(x => x.NormalizedEmail == emailNorm && x.ClientUserId != oldClientUserId);

        if (emailCollision)
            return Conflict("That email is already tied to another client record.");

        var firstName = Norm(profile.FirstName);
        var lastName = Norm(profile.LastName);
        var oneTimePassword = GenerateOneTimePassword();
        string? newClientObjectId = null;
        string? loginUpn = null;
        var createdGraphUser = false;
        var committed = false;

        try
        {
            var personalEmail = emailNorm
                ?? throw new InvalidOperationException("Portal client email is required before enabling portal access.");

            (newClientObjectId, loginUpn) = await _provisioning.CreateTenantUserAsync(
                firstName,
                lastName,
                personalEmail,
                oneTimePassword
            );

            newClientObjectId = NormLower(newClientObjectId);
            loginUpn = Norm(loginUpn);

            if (string.IsNullOrWhiteSpace(newClientObjectId))
                throw new Exception("Provisioning returned an empty client user id.");

            if (string.IsNullOrWhiteSpace(loginUpn))
                throw new Exception("Provisioning returned an empty login UPN.");

            createdGraphUser = true;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
            meta.RecordType = recordType;
            meta.PipelineStage = DefaultPipelineStageForRecordType(recordType);
            meta.StageEnteredUtc = DateTime.UtcNow;
            var agentLinks = await _db.AgentClients.Where(x => x.ClientUserId == oldClientUserId).ToListAsync();
            var householdMembers = await _db.HouseholdMembers.Where(x => x.ClientUserId == oldClientUserId).ToListAsync();

            var recreatedLinks = agentLinks.Select(link => new AgentClient
            {
                AgentUserId = link.AgentUserId,
                ClientUserId = newClientObjectId,
                AgentUpn = link.AgentUpn,
                CreatedUtc = link.CreatedUtc
            }).ToList();

            var recreatedHousehold = householdMembers.Select(member => new HouseholdMember
            {
                ClientUserId = newClientObjectId,
                RelationshipType = member.RelationshipType,
                FirstName = member.FirstName,
                LastName = member.LastName,
                DOB = member.DOB,
                Email = member.Email,
                Phone = member.Phone,
                CreatedUtc = member.CreatedUtc,
                UpdatedUtc = DateTime.UtcNow
            }).ToList();

            if (agentLinks.Count > 0)
                _db.AgentClients.RemoveRange(agentLinks);

            if (householdMembers.Count > 0)
                _db.HouseholdMembers.RemoveRange(householdMembers);

            await _db.SaveChangesAsync();

            var updatedUtc = DateTime.UtcNow;
            var serializedMeta = ClientCrmMetaSerializer.Serialize(meta);
            var profileId = profile.Id;

            _db.Entry(profile).State = EntityState.Detached;

            var updatedRows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ClientProfiles
                SET ClientUserId = {newClientObjectId},
                    CrmNotes = {serializedMeta},
                    CrmStatus = {"Active"},
                    UpdatedUtc = {updatedUtc}
                WHERE Id = {profileId}");

            if (updatedRows != 1)
                throw new Exception("Portal conversion failed while updating the client profile record.");

            if (recreatedLinks.Count > 0)
                _db.AgentClients.AddRange(recreatedLinks);

            if (recreatedHousehold.Count > 0)
                _db.HouseholdMembers.AddRange(recreatedHousehold);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            committed = true;

            var clientPortalUrl = GetClientPortalBaseUrl();
            string? emailWarning = null;

            try
            {
                await _provisioning.SendClientWelcomeEmailAsync(
                    emailNorm,
                    firstName,
                    loginUpn,
                    oneTimePassword,
                    clientPortalUrl,
                    newClientObjectId,
                    forceIdLink: true
                );
            }
            catch (Exception mailEx)
            {
                _logger.LogError(
                    mailEx,
                    "Portal access email failed after successful conversion. AgentOid={AgentOid} Email={Email} ClientUserId={ClientUserId}",
                    agentOid,
                    emailNorm,
                    newClientObjectId
                );

                emailWarning = $"Portal access was enabled, but the welcome email failed to send: {mailEx.Message}";
            }

            return Json(new
            {
                oldClientUserId,
                newClientUserId = newClientObjectId,
                recordType,
                recordTypeLabel = RecordTypeLabel(recordType),
                loginUpn,
                oneTimePassword,
                portalAccessEnabled = true,
                pipelineStage = DefaultPipelineStageForRecordType(recordType),
                pipelineStageLabel = StageLabel(DefaultPipelineStageForRecordType(recordType)),
                clientPortalUrl = $"{clientPortalUrl}/support/view-as-client/{profile.Id}",
                emailSent = string.IsNullOrWhiteSpace(emailWarning),
                warning = emailWarning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "EnablePortalAccess failed. AgentOid={AgentOid} Email={Email} ClientUserId={ClientUserId}",
                agentOid,
                emailNorm,
                oldClientUserId
            );

            if (!committed && createdGraphUser && !string.IsNullOrWhiteSpace(newClientObjectId))
            {
                try { await _provisioning.DeleteTenantUserAsync(newClientObjectId); } catch { }
            }

            return StatusCode(StatusCodes.Status500InternalServerError,
                $"Failed to convert this lead into a portal {RecordTypeLabel(recordType).ToLowerInvariant()}: {ex.Message}");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuickView([FromBody] QuickViewRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);
        if (profile == null) return Forbid();

        var emailNorm = NormLower(request.Email);
        if (!string.IsNullOrWhiteSpace(emailNorm))
        {
            var exists = await _db.ClientProfiles
                .AsNoTracking()
                .AnyAsync(x => x.Email == emailNorm && x.ClientUserId != profile.ClientUserId);
            if (exists)
                return BadRequest("Email already exists on another client.");
        }

        var allowedStatus = new[] { "Lead", "Prospect", "Active", "Dormant" };
        var allowedPriority = new[] { "Low", "Normal", "High", "Urgent" };

        var crmStatus = (request.CrmStatus ?? "").Trim();
        if (!allowedStatus.Contains(crmStatus, StringComparer.OrdinalIgnoreCase))
            crmStatus = "Lead";

        var crmPriority = (request.CrmPriority ?? "").Trim();
        if (!allowedPriority.Contains(crmPriority, StringComparer.OrdinalIgnoreCase))
            crmPriority = "Normal";

        var crmNextText = (request.CrmNextText ?? "").Trim();
        var crmTags = NormalizeTags(request.CrmTags);
        var agentNotes = (request.AgentNotes ?? "").Trim();

        if (request.CrmNextDate.HasValue && string.IsNullOrWhiteSpace(crmNextText))
            return BadRequest("Next action text is required when next action date is set.");

        if (!request.CrmNextDate.HasValue && !string.IsNullOrWhiteSpace(crmNextText))
            return BadRequest("Next action date is required when next action text is set.");

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        var currentRecordType = ResolveRecordType(profile.ClientUserId, meta);
        var normalizedRecordType = string.IsNullOrWhiteSpace(request.RecordType)
            ? currentRecordType
            : NormalizeRecordType(request.RecordType);
        var normalizedStage = NormalizePipelineStage(request.PipelineStage);

        if (!HasPortalAccess(profile.ClientUserId))
        {
            normalizedRecordType = "Lead";
            if (normalizedStage is "Client" or "BusinessClient")
                normalizedStage = ClientCrmMeta.DefaultPipelineStage;
        }
        else
        {
            if (!IsPortalRecordType(normalizedRecordType))
                normalizedRecordType = currentRecordType;

            var currentStage = NormalizePipelineStage(meta.PipelineStage);
            if (!string.Equals(currentRecordType, normalizedRecordType, StringComparison.OrdinalIgnoreCase) &&
                (currentStage is "Client" or "BusinessClient" || normalizedStage is "Client" or "BusinessClient"))
            {
                normalizedStage = DefaultPipelineStageForRecordType(normalizedRecordType);
            }
        }

        if (!string.Equals(meta.PipelineStage, normalizedStage, StringComparison.Ordinal))
            meta.StageEnteredUtc = DateTime.UtcNow;
        meta.RecordType = normalizedRecordType;
        meta.PipelineStage = normalizedStage;
        meta.PipelineOrder = request.PipelineOrder ?? meta.PipelineOrder;
        meta.WaitingOn = NormalizeWaitingOn(request.WaitingOn);
        meta.PinnedBrief = string.IsNullOrWhiteSpace(request.PinnedBrief) ? null : request.PinnedBrief.Trim();
        meta.MeetingLocation = string.IsNullOrWhiteSpace(request.MeetingLocation) ? null : request.MeetingLocation.Trim();
        meta.ZoomJoinUrl = string.IsNullOrWhiteSpace(request.ZoomJoinUrl) ? null : request.ZoomJoinUrl.Trim();
        meta.UsePersonalZoomLink = request.UsePersonalZoomLink;
        meta.MeetingTime = string.IsNullOrWhiteSpace(request.MeetingTime) ? "09:00" : request.MeetingTime.Trim();
        meta.MeetingDurationMinutes = request.MeetingDurationMinutes <= 0 ? 30 : request.MeetingDurationMinutes;
        meta.DocChecklist.IdReceived = request.DocIdReceived;
        meta.DocChecklist.AppSent = request.DocAppSent;
        meta.DocChecklist.AppSigned = request.DocAppSigned;
        meta.DocChecklist.PolicyDelivered = request.DocPolicyDelivered;
        meta.DocChecklist.ReviewBooked = request.DocReviewBooked;
        if (request.OpportunityPlanning != null)
        {
            meta.OpportunityPlanning.LifeInsurance = request.OpportunityPlanning.LifeInsurance;
            meta.OpportunityPlanning.DisabilityIncome = request.OpportunityPlanning.DisabilityIncome;
            meta.OpportunityPlanning.LongTermCare = request.OpportunityPlanning.LongTermCare;
            meta.OpportunityPlanning.CriticalIllness = request.OpportunityPlanning.CriticalIllness;
            meta.OpportunityPlanning.TerminalIllness = request.OpportunityPlanning.TerminalIllness;
            meta.OpportunityPlanning.AnnuityRetirement = request.OpportunityPlanning.AnnuityRetirement;
            meta.OpportunityPlanning.MortgageProtection = request.OpportunityPlanning.MortgageProtection;
            meta.OpportunityPlanning.FinalExpense = request.OpportunityPlanning.FinalExpense;
            meta.OpportunityPlanning.Medicare = request.OpportunityPlanning.Medicare;
            meta.OpportunityPlanning.Health = request.OpportunityPlanning.Health;
            meta.OpportunityPlanning.DentalVision = request.OpportunityPlanning.DentalVision;
            meta.OpportunityPlanning.HospitalIndemnity = request.OpportunityPlanning.HospitalIndemnity;
            meta.OpportunityPlanning.PersonalAuto = request.OpportunityPlanning.PersonalAuto;
            meta.OpportunityPlanning.HomeRenters = request.OpportunityPlanning.HomeRenters;
            meta.OpportunityPlanning.UmbrellaLiability = request.OpportunityPlanning.UmbrellaLiability;
            meta.OpportunityPlanning.FloodEarthquake = request.OpportunityPlanning.FloodEarthquake;
            meta.OpportunityPlanning.CommercialAuto = request.OpportunityPlanning.CommercialAuto;
            meta.OpportunityPlanning.GeneralLiability = request.OpportunityPlanning.GeneralLiability;
            meta.OpportunityPlanning.BusinessOwnersPolicy = request.OpportunityPlanning.BusinessOwnersPolicy;
            meta.OpportunityPlanning.WorkersComp = request.OpportunityPlanning.WorkersComp;
            meta.OpportunityPlanning.KeyPersonBuySell = request.OpportunityPlanning.KeyPersonBuySell;
            meta.OpportunityPlanning.GroupBenefits = request.OpportunityPlanning.GroupBenefits;
        }
        meta.Collaboration.Owner = string.IsNullOrWhiteSpace(meta.Collaboration.Owner)
            ? GetAgentUpnForAudit()
            : meta.Collaboration.Owner.Trim();
        meta.Collaboration.Watchers = (request.Watchers ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(request.MentionNote))
        {
            meta.Collaboration.MentionNotes.Insert(0, new ClientCrmMentionNote
            {
                Note = request.MentionNote.Trim(),
                MentionedUser = meta.Collaboration.Watchers.FirstOrDefault(),
                CreatedBy = GetAgentUpnForAudit()
            });
        }

        profile.CrmStatus = crmStatus;
        profile.CrmPriority = crmPriority;
        profile.CrmLastTouch = request.CrmLastTouch;
        profile.CrmNextDate = request.CrmNextDate;
        profile.CrmNextText = string.IsNullOrWhiteSpace(crmNextText) ? null : crmNextText;
        profile.CrmTags = string.IsNullOrWhiteSpace(crmTags) ? null : crmTags;
        profile.AgentNotes = agentNotes;
        profile.Phone = (request.Phone ?? profile.Phone ?? "").Trim();
        profile.Email = string.IsNullOrWhiteSpace(emailNorm) ? (profile.Email ?? "") : emailNorm;
        profile.NormalizedEmail = NormalizeEmail(profile.Email);
        if (string.IsNullOrWhiteSpace(profile.Email))
        {
            profile.Email = $"{profile.ClientUserId}@leads.local";
            profile.NormalizedEmail = profile.Email.ToLowerInvariant();
        }
        var existingDob = profile.DOB?.Date;
        var requestedDob = request.Dob?.Date;
        if (existingDob.HasValue)
        {
            if (requestedDob.HasValue && existingDob.Value != requestedDob.Value)
                return BadRequest("DOB is locked after initial entry. Contact support to update it.");
        }
        else if (requestedDob.HasValue)
        {
            profile.DOB = requestedDob.Value;
        }
        meta.Gender = request.Gender;
        meta.AddressLine = request.AddressLine;
        meta.City = request.City;
        meta.State = request.State;
        meta.County = request.County;
        meta.ZipCode = request.ZipCode;
        meta.Phone2 = string.IsNullOrWhiteSpace(request.Phone2) ? null : request.Phone2.Trim();
        meta.Age = string.IsNullOrWhiteSpace(request.Age) ? null : request.Age.Trim();
        meta.Btc = string.IsNullOrWhiteSpace(request.Btc) ? null : request.Btc.Trim();
        meta.MortgageLender = request.MortgageLender;
        meta.LoanAmount = request.LoanAmount;
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        profile.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        return Json(new
        {
            ok = true,
            payload = BuildQuickViewPayload(profile, meta, dialTimeZone, nowUtc)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddActivity([FromBody] ActivityRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);
        if (profile == null) return Forbid();

        var note = (request.Note ?? "").Trim();
        if (string.IsNullOrWhiteSpace(note))
            return BadRequest("Activity note is required.");

        var type = (request.Type ?? "").Trim();
        if (string.IsNullOrWhiteSpace(type))
            type = "Note";

        var dateRaw = request.Date ?? "";
        var date = Regex.IsMatch(dateRaw, @"^\d{4}-\d{2}-\d{2}$")
            ? dateRaw.Trim()
            : DateTime.UtcNow.ToString("yyyy-MM-dd");

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        meta.Activities.Add(new ClientCrmActivity
        {
            Type = type,
            Date = date,
            Note = note,
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            MeetingLink = string.IsNullOrWhiteSpace(request.MeetingLink) ? null : request.MeetingLink.Trim(),
            CalendarEventId = string.IsNullOrWhiteSpace(request.CalendarEventId) ? null : request.CalendarEventId.Trim(),
            CalendarWebLink = string.IsNullOrWhiteSpace(request.CalendarWebLink) ? null : request.CalendarWebLink.Trim(),
            Channel = type,
            CreatedBy = GetAgentUpnForAudit()
        });

        profile.CrmLastTouch = DateTime.TryParse(date, out var touchDate) ? touchDate.Date : profile.CrmLastTouch;
        meta.LastContactChannel = type;
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        profile.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);
        var attemptCounts = CrmAttemptTracking.CountClientActivityAttempts(meta.Activities, IsContactAttempt, nowUtc, dialTimeZone);

        return Json(new
        {
            ok = true,
            crmLastTouch = profile.CrmLastTouch?.ToString("yyyy-MM-dd"),
            attemptsToday = attemptCounts.Today,
            attemptsThisWeek = attemptCounts.Week,
            attemptsThisMonth = attemptCounts.Month,
            attemptsThisYear = attemptCounts.Year,
            attemptsLifetime = attemptCounts.Lifetime,
            activities = meta.Activities
                .OrderByDescending(x => x.Date)
                .ThenByDescending(x => x.CreatedUtc)
                .ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearActivities([FromBody] ClientRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);
        if (profile == null) return Forbid();

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        meta.Activities.Clear();
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        profile.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Json(new
        {
            ok = true,
            attemptsToday = 0,
            attemptsThisWeek = 0,
            attemptsThisMonth = 0,
            attemptsThisYear = 0,
            attemptsLifetime = 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyOutcome([FromBody] OutcomeRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var profile = await GetOwnedClientProfileAsync(agentOid, request.ClientUserId);
        if (profile == null) return Forbid();

        var plan = BuildOutcomePlan(request.OutcomeCode);
        if (plan == null) return BadRequest("Unknown outcome.");

        var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));
        if (!string.Equals(meta.PipelineStage, plan.PipelineStage, StringComparison.Ordinal))
            meta.StageEnteredUtc = DateTime.UtcNow;

        meta.PipelineStage = plan.PipelineStage;
        meta.WaitingOn = plan.WaitingOn;
        meta.LastContactChannel = plan.ContactChannel;
        meta.MeetingLocation = string.IsNullOrWhiteSpace(request.MeetingLocation) ? meta.MeetingLocation : request.MeetingLocation.Trim();
        meta.ZoomJoinUrl = string.IsNullOrWhiteSpace(request.ZoomJoinUrl) ? meta.ZoomJoinUrl : request.ZoomJoinUrl.Trim();
        meta.UsePersonalZoomLink = request.UsePersonalZoomLink;
        meta.MeetingTime = string.IsNullOrWhiteSpace(request.MeetingTime) ? meta.MeetingTime : request.MeetingTime.Trim();
        meta.MeetingDurationMinutes = request.MeetingDurationMinutes <= 0 ? meta.MeetingDurationMinutes : request.MeetingDurationMinutes;

        if (plan.ClearMeetingDetails)
        {
            meta.MeetingLocation = null;
            meta.ZoomJoinUrl = null;
        }

        var note = string.IsNullOrWhiteSpace(request.CustomNote)
            ? plan.ActivityNote
            : $"{plan.ActivityNote} {request.CustomNote.Trim()}".Trim();

        meta.Activities.Insert(0, new ClientCrmActivity
        {
            Type = plan.ActivityType,
            Date = ToIsoDate(DateTime.UtcNow.Date),
            Note = note,
            OutcomeCode = request.OutcomeCode,
            Channel = plan.ContactChannel,
            IsSystem = true,
            Location = meta.MeetingLocation,
            MeetingLink = meta.ZoomJoinUrl,
            CreatedBy = GetAgentUpnForAudit()
        });

        if (!string.IsNullOrWhiteSpace(plan.CrmStatus))
            profile.CrmStatus = plan.CrmStatus;

        profile.CrmLastTouch = DateTime.UtcNow.Date;
        profile.CrmNextDate = plan.NextActionDate;
        profile.CrmNextText = plan.NextActionText;
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
        profile.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var nowUtc = DateTime.UtcNow;
        var dialTimeZone = _agentTimeZoneResolver.Resolve(HttpContext);

        return Json(new
        {
            ok = true,
            outcomeCode = request.OutcomeCode,
            suggestion = new
            {
                pipelineStage = plan.PipelineStage,
                pipelineStageLabel = StageLabel(plan.PipelineStage),
                waitingOn = plan.WaitingOn,
                waitingOnLabel = WaitingOnLabel(plan.WaitingOn),
                nextDate = ToIsoDate(plan.NextActionDate),
                nextText = plan.NextActionText,
                meetingTime = meta.MeetingTime ?? "09:00",
                meetingDurationMinutes = meta.MeetingDurationMinutes
            },
            payload = BuildQuickViewPayload(profile, meta, dialTimeZone, nowUtc)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkUpdateRequest request)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientIds = (request.ClientUserIds ?? new List<string>())
            .Select(NormLower)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        if (clientIds.Count == 0)
            return BadRequest("No clients selected.");

        var ownedClientIds = await _db.AgentClients
            .Where(x => x.AgentUserId == agentOid && clientIds.Contains(x.ClientUserId))
            .Select(x => x.ClientUserId)
            .ToListAsync();

        var profiles = await _db.ClientProfiles
            .Where(x => ownedClientIds.Contains(x.ClientUserId))
            .ToListAsync();

        var normalizedStage = string.IsNullOrWhiteSpace(request.PipelineStage) ? null : NormalizePipelineStage(request.PipelineStage);
        var normalizedPriority = string.IsNullOrWhiteSpace(request.CrmPriority) ? null : request.CrmPriority.Trim();
        var normalizedTags = string.IsNullOrWhiteSpace(request.CrmTags) ? null : NormalizeTags(request.CrmTags);
        var normalizedWaiting = string.IsNullOrWhiteSpace(request.WaitingOn) ? null : NormalizeWaitingOn(request.WaitingOn);
        var sharedNote = string.IsNullOrWhiteSpace(request.SharedNote) ? null : request.SharedNote.Trim();
        var nextText = string.IsNullOrWhiteSpace(request.CrmNextText) ? null : request.CrmNextText.Trim();

        foreach (var profile in profiles)
        {
            var meta = EnsureMeta(ClientCrmMetaSerializer.Deserialize(profile.CrmNotes));

            if (!string.IsNullOrWhiteSpace(normalizedStage) && !string.Equals(meta.PipelineStage, normalizedStage, StringComparison.Ordinal))
            {
                meta.PipelineStage = normalizedStage;
                meta.StageEnteredUtc = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(normalizedWaiting))
                meta.WaitingOn = normalizedWaiting;

            if (!string.IsNullOrWhiteSpace(normalizedPriority))
                profile.CrmPriority = normalizedPriority;

            if (!string.IsNullOrWhiteSpace(normalizedTags))
                profile.CrmTags = normalizedTags;

            if (request.CrmNextDate.HasValue)
                profile.CrmNextDate = request.CrmNextDate.Value.Date;

            if (!string.IsNullOrWhiteSpace(nextText))
                profile.CrmNextText = nextText;

            if (!string.IsNullOrWhiteSpace(sharedNote))
            {
                meta.Activities.Insert(0, new ClientCrmActivity
                {
                    Type = "Note",
                    Date = ToIsoDate(DateTime.UtcNow.Date),
                    Note = $"Bulk note: {sharedNote}",
                    IsSystem = true,
                    Channel = "System",
                    CreatedBy = GetAgentUpnForAudit()
                });
            }

            profile.CrmNotes = ClientCrmMetaSerializer.Serialize(meta);
            profile.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return Json(new
        {
            ok = true,
            updatedCount = profiles.Count
        });
    }

    // =====================================================================
    // POST: /Clients/Delete
    // =====================================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string clientUserId)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var clientUserIdNorm = NormLower(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserIdNorm))
            return RedirectToAction(nameof(Index));

        // Ownership check
        var linked = await _db.AgentClients.AnyAsync(x =>
            x.AgentUserId == agentOid &&
            x.ClientUserId == clientUserIdNorm);

        if (!linked)
            return Forbid();

        await using var tx = await _db.Database.BeginTransactionAsync();

        try
        {
            // Safety: ensure only one owner link exists before deleting Entra
            var linkCount = await _db.AgentClients.CountAsync(x => x.ClientUserId == clientUserIdNorm);
            if (linkCount != 1)
                throw new Exception("Safety stop: client is linked to multiple agents. Refusing to delete Entra user.");

            // Delete Entra user
            await _provisioning.DeleteTenantUserAsync(clientUserIdNorm);

            // DB cleanup
            var allLinks = await _db.AgentClients
                .Where(x => x.ClientUserId == clientUserIdNorm)
                .ToListAsync();
            if (allLinks.Count > 0)
                _db.AgentClients.RemoveRange(allLinks);

            var household = await _db.HouseholdMembers
                .Where(x => x.ClientUserId == clientUserIdNorm)
                .ToListAsync();
            if (household.Count > 0)
                _db.HouseholdMembers.RemoveRange(household);

            var profile = await _db.ClientProfiles
                .FirstOrDefaultAsync(x => x.ClientUserId == clientUserIdNorm);
            if (profile != null)
                _db.ClientProfiles.Remove(profile);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            TempData["Created"] = "Client deleted (profile + household + client shared finance + Entra account removed).";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed. AgentOid={AgentOid} ClientUserId={ClientUserId}", agentOid, clientUserIdNorm);

            await tx.RollbackAsync();
            TempData["Created"] = $"Delete failed: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }
    }
}
