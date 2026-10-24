using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Constructed only by an authenticated provider adapter after raw-body signature/replay validation.</summary>
internal sealed record TrustedReceptionistIngress(
    string Provider, string IngressKey, string ProviderProjectKey,
    string ProviderSessionId, string EventId, bool ControlledTest,
    Guid AuthenticatedConnectionId, Guid AuthenticatedConfigurationId, string AuthenticatedOwnerKey);

/// <summary>Private verified provider configuration. Immutable encrypted credentials are never returned in owner snapshots.</summary>
internal sealed record ReceptionistProviderBinding
{
    public string IngressKey { get; init; } = string.Empty;
    public string ProviderProjectKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Voice { get; init; } = string.Empty;
    public string BackendMode { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string CredentialReference { get; init; } = string.Empty;
    public string WebhookSecretReference { get; init; } = string.Empty;
    public string? ProtectedApiCredential { get; init; }
    public string? ProtectedWebhookSecret { get; init; }
    public string? OperatorAcceptancePackageSha256 { get; init; }
    public string? ContentReviewHash { get; init; }
    public Guid? ContentSourceConfigurationId { get; init; }
    public string? ContentSourceConfigurationSha256 { get; init; }
    public string? ContentSourceBindingSha256 { get; init; }
    public string? ContentSourceOperatorAcceptanceSha256 { get; init; }
    public string? ContentSourceDisclosureReceipt { get; init; }
    public string? ContentReviewedConfigurationSha256 { get; init; }
    public string ProviderVerificationReceipt { get; init; } = string.Empty;
    public string ExclusiveProjectVerificationReceipt { get; init; } = string.Empty;
    public string CostVerificationReceipt { get; init; } = string.Empty;
    public string DisclosureConfigurationReceipt { get; init; } = string.Empty;
    public string RetentionConfigurationReceipt { get; init; } = string.Empty;
    public string CarrierFallbackReceipt { get; init; } = string.Empty;
    public int MaximumSupportedCallSeconds { get; init; }
    public bool ControlledTestOnly { get; init; } = true;
    public string? PublicActivationReceipt { get; init; }
    public IReadOnlyList<ReceptionistCostComponent> Costs { get; init; } = [];
    public IReadOnlyList<ReceptionistAcceptedCapability> Capabilities { get; init; } = [];
}

internal sealed record ReceptionistCostComponent(
    string Key, Guid FundingBudgetAccountId,
    decimal CashFixedMaximum, decimal CashPerSecondMaximum,
    decimal ConsumptionFixedMaximum, decimal ConsumptionPerSecondMaximum);

internal sealed record ReceptionistAcceptedCapability(
    string Key, string RouteKey, string State, string AcceptanceReceipt);

internal sealed record ReceptionistAdmission(
    bool Reserved, bool MayAcceptProvider, string Reason, Guid? CallId,
    Guid? ConfigurationId = null, DateTime? MustStopUtc = null);

internal sealed record ReceptionistActionClaim(
    bool MayExecute, string State, Guid? ActionId, string? RouteKey = null,
    string? ActualRecordReference = null, string? VerificationReceipt = null, Guid? AttemptId = null);

internal sealed record ReceptionistStopRequest(Guid CallId, string Provider, string ProviderProjectKey,
    string ProviderSessionId, Guid ConfigurationId, string Reason);

internal sealed record ReceptionistCallContext(Guid CallId, MarketingOwnerScope Owner, string Provider,
    string ProviderProjectKey, string ProviderSessionId, Guid ConfigurationId,
    ReceptionistBusinessConfiguration Configuration, ReceptionistProviderBinding Binding,
    DateTime MustStopUtc, string State, bool ControlledTest);

internal sealed record ReceptionistIngressBinding(string Provider, Guid MarketingConnectionId,
    MarketingOwnerScope Owner, Guid ConfigurationId, ReceptionistBusinessConfiguration Configuration,
    ReceptionistProviderBinding Binding);

internal sealed record ReceptionistProviderAcceptClaim(bool MayExecute, string State, Guid CallId, Guid? AttemptId);
internal sealed record ReceptionistSidebandLease(Guid CallId, string WorkerId, Guid Token, long Fence, DateTime ExpiresUtc, bool ObservationOnly);
internal sealed record ReceptionistProviderWorkItem(Guid CallId, string State, DateTime MustStopUtc);
internal sealed record ReceptionistVerifiedProviderObservation(string Receipt, DateTime ObservedUtc, bool Active);
internal sealed record ReceptionistClosureReconciliation(Guid CallId, bool VoiceUsageFinal, bool VoiceAllocationUncertain);

/// <summary>Only the existing authorized accounting/configuration path may provide an opening or change shared funding.</summary>
internal sealed record ReceptionistBudgetOpening(
    string Kind, string ScopeKey, string Currency, string TimeZoneId, int Year, int Month,
    decimal CashLimit, decimal ConsumptionLimit,
    decimal KnownCashCommitted, decimal KnownConsumptionCommitted, long KnownVoiceSeconds,
    string ReconciliationReceipt, DateTime ReconciledThroughUtc);

internal sealed record ReceptionistVerifiedAccountCharge(
    Guid OwnerBudgetAccountId, Guid FundingBudgetAccountId,
    string ProviderChargeKey, string ComponentKey, string ProviderReceiptKey,
    DateTime? CashDebitedUtc, decimal CumulativeCash,
    DateTime? ConsumptionIncurredUtc, decimal CumulativeConsumption,
    bool CashFinal = false, bool ConsumptionFinal = false);

/// <summary>Per calendar-pair cumulative totals. Final component totals must account for every dated posting.</summary>
internal sealed record ReceptionistDatedCostObservation(
    DateTime? CashDebitedUtc, decimal CumulativeCashForPeriod,
    DateTime? ConsumptionIncurredUtc, decimal CumulativeConsumptionForPeriod,
    decimal? FinalCashTotal = null, decimal? FinalConsumptionTotal = null);

/// <summary>Verified cumulative duration allocated to the owner's actual service period, not receipt-arrival time.</summary>
internal sealed record ReceptionistDatedVoiceUsage(DateTime ServicePeriodEvidenceUtc, long CumulativeSecondsForPeriod);

internal static class ReceptionistConfigurationCodec
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string Serialize(ReceptionistBusinessConfiguration value)
    {
        Validate(value);
        return JsonSerializer.Serialize(value, Json);
    }

    internal static ReceptionistBusinessConfiguration Read(string json, string expectedHash)
    {
        if (!string.Equals(Hash(json), expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Receptionist configuration fingerprint does not match.");
        var value = JsonSerializer.Deserialize<ReceptionistBusinessConfiguration>(json, Json)
            ?? throw new InvalidOperationException("Receptionist configuration is absent.");
        Validate(value);
        return value;
    }

    internal static ReceptionistProviderBinding? ReadBinding(string? json,
        ReceptionistBusinessConfiguration configuration)
    {
        if (json is null) return null;
        var binding = JsonSerializer.Deserialize<ReceptionistProviderBinding>(json, Json)
            ?? throw new InvalidOperationException("Receptionist provider binding is absent.");
        ValidateBinding(binding, configuration);
        return binding;
    }

    internal static void Validate(ReceptionistBusinessConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Required(value.BusinessName, 300, nameof(value.BusinessName));
        Required(value.ConversationInstructions, 65536, nameof(value.ConversationInstructions), allowLines: true);
        Required(value.BusinessPolicy, 262144, nameof(value.BusinessPolicy), allowLines: true);
        if (value.CompactVoicePolicy is not null)
        {
            Required(value.CompactVoicePolicy, 8000, nameof(value.CompactVoicePolicy), allowLines: true);
            if (value.CompactVoicePolicySourceSha256 != Hash(value.BusinessPolicy))
                throw new ArgumentException("The compact voice policy must be reviewed against the current full business policy.");
        }
        else if (value.CompactVoicePolicySourceSha256 is not null)
            throw new ArgumentException("A compact policy reference requires its reviewed text.");
        JsonObjectOrArray(value.ApprovedKnowledgeJson, nameof(value.ApprovedKnowledgeJson));
        JsonObjectOrArray(value.ServiceDefinitionsJson, nameof(value.ServiceDefinitionsJson));
        JsonObjectOrArray(value.IntakeRulesJson, nameof(value.IntakeRulesJson));
        JsonObjectOrArray(value.ContactAndRetentionPolicyJson, nameof(value.ContactAndRetentionPolicyJson));
        Currency(value.Currency);
        TimeZoneInfo.FindSystemTimeZoneById(Required(value.TimeZoneId, 200, nameof(value.TimeZoneId)));
        Money(value.MonthlyCashLimit); Money(value.MonthlyConsumptionLimit);
        if (value.MonthlyVoiceSecondsLimit < 0 || value.MaximumCallSeconds <= 0 ||
            value.MaximumConcurrentCalls <= 0 || value.AdmissionWindowSeconds <= 0 || value.MaximumCallsPerWindow <= 0)
            throw new ArgumentException("Receptionist duration, concurrency and admission-window limits must be explicit and positive.");
        if (value.Hours is null || value.Hours.Count > 168 || value.Hours.Any(x => x is null ||
            !Enum.IsDefined(x.Day) || x.Start == x.End))
            throw new ArgumentException("Reception hours must contain valid, bounded local-time windows.");
        if (value.RequestedCapabilities is null || value.RequestedCapabilities.Count > 100 ||
            value.RequestedCapabilities.Distinct(StringComparer.Ordinal).Count() != value.RequestedCapabilities.Count)
            throw new ArgumentException("Requested capabilities must be unique.");
        foreach (var key in value.RequestedCapabilities)
        {
            Identifier(key, 100, "capability");
            if (key.StartsWith("__", StringComparison.Ordinal)) throw new ArgumentException("Provider coordination keys are not business capabilities.");
        }
    }

    internal static void ValidateBinding(ReceptionistProviderBinding value, ReceptionistBusinessConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasContentProvenance(value))
        {
            static bool Sha(string? text) => text is { Length: 64 } && text.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
            if (!Sha(value.ContentReviewHash) || value.ContentSourceConfigurationId == Guid.Empty ||
                !Sha(value.ContentSourceConfigurationSha256) || !Sha(value.ContentSourceBindingSha256) ||
                !Sha(value.ContentReviewedConfigurationSha256) || value.ContentSourceConfigurationId is null ||
                value.ContentReviewedConfigurationSha256 != Hash(Serialize(configuration)) ||
                value.OperatorAcceptancePackageSha256 is not null || string.IsNullOrWhiteSpace(value.ContentSourceDisclosureReceipt) ||
                (value.ContentSourceOperatorAcceptanceSha256 is not null && !Sha(value.ContentSourceOperatorAcceptanceSha256)) ||
                value.DisclosureConfigurationReceipt != "owner-content-review:v1:" + value.ContentReviewHash)
                throw new ArgumentException("Content review provenance must identify the complete reviewed configuration and preserved source authority.");
        }
        Required(value.IngressKey, 200, nameof(value.IngressKey));
        Required(value.ProviderProjectKey, 200, nameof(value.ProviderProjectKey));
        Required(value.Model, 100, nameof(value.Model));
        Required(value.Voice, 100, nameof(value.Voice));
        Identifier(value.BackendMode, 100, nameof(value.BackendMode));
        Identifier(value.Transport, 40, nameof(value.Transport));
        Required(value.CredentialReference, 512, nameof(value.CredentialReference));
        Required(value.WebhookSecretReference, 512, nameof(value.WebhookSecretReference));
        Required(value.ProviderVerificationReceipt, 512, nameof(value.ProviderVerificationReceipt));
        Required(value.ExclusiveProjectVerificationReceipt, 512, nameof(value.ExclusiveProjectVerificationReceipt));
        Required(value.CostVerificationReceipt, 512, nameof(value.CostVerificationReceipt));
        Required(value.DisclosureConfigurationReceipt, 512, nameof(value.DisclosureConfigurationReceipt));
        Required(value.RetentionConfigurationReceipt, 512, nameof(value.RetentionConfigurationReceipt));
        Required(value.CarrierFallbackReceipt, 512, nameof(value.CarrierFallbackReceipt));
        if (!value.ControlledTestOnly) Required(value.PublicActivationReceipt, 512, nameof(value.PublicActivationReceipt));
        if (value.MaximumSupportedCallSeconds < configuration.MaximumCallSeconds)
            throw new ArgumentException("The requested duration exceeds the verified provider capability.");
        if (value.Costs is null || value.Costs.Count == 0 || value.Costs.Count > 32 || value.Costs.Any(x => x is null) ||
            value.Costs.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != value.Costs.Count)
            throw new ArgumentException("A complete unique provider cost-component map is required.");
        foreach (var component in value.Costs)
        {
            Identifier(component.Key, 100, "cost component");
            if (component.FundingBudgetAccountId == Guid.Empty) throw new ArgumentException("A verified funding account is required.");
            Money(component.CashFixedMaximum); Money(component.CashPerSecondMaximum);
            Money(component.ConsumptionFixedMaximum); Money(component.ConsumptionPerSecondMaximum);
            Maximum(component.CashFixedMaximum, component.CashPerSecondMaximum, configuration.MaximumCallSeconds);
            Maximum(component.ConsumptionFixedMaximum, component.ConsumptionPerSecondMaximum, configuration.MaximumCallSeconds);
        }
        if (value.Capabilities is null || value.Capabilities.Count > 100 || value.Capabilities.Any(x => x is null) ||
            value.Capabilities.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != value.Capabilities.Count)
            throw new ArgumentException("Accepted capability bindings must be unique.");
        foreach (var capability in value.Capabilities)
        {
            Identifier(capability.Key, 100, "capability");
            if (capability.State is not (ReceptionistCapabilityStates.Disabled or ReceptionistCapabilityStates.Unverified or ReceptionistCapabilityStates.Accepted))
                throw new ArgumentException("Unsupported capability acceptance state.");
            if (capability.State == ReceptionistCapabilityStates.Accepted)
            {
                Identifier(capability.RouteKey, 160, "canonical route");
                Required(capability.AcceptanceReceipt, 512, "capability acceptance receipt");
            }
        }
    }

    internal static bool HasContentProvenance(ReceptionistProviderBinding value) =>
        value.ContentReviewHash is not null || value.ContentSourceConfigurationId is not null ||
        value.ContentSourceConfigurationSha256 is not null || value.ContentSourceBindingSha256 is not null ||
        value.ContentSourceOperatorAcceptanceSha256 is not null || value.ContentSourceDisclosureReceipt is not null ||
        value.ContentReviewedConfigurationSha256 is not null;

    internal static bool HasRequiredIntake(ReceptionistBusinessConfiguration configuration, ReceptionistProviderBinding binding) =>
        configuration.RequestedCapabilities.Contains(ReceptionistCapabilities.CanonicalIntake, StringComparer.Ordinal) &&
        binding.Capabilities.Any(x => x.Key == ReceptionistCapabilities.CanonicalIntake && x.State == ReceptionistCapabilityStates.Accepted);

    internal static bool IsOpen(ReceptionistBusinessConfiguration configuration, DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId));
        var time = TimeOnly.FromDateTime(local);
        var previous = (DayOfWeek)(((int)local.DayOfWeek + 6) % 7);
        return configuration.Hours.Any(window => window.Start < window.End
            ? window.Day == local.DayOfWeek && time >= window.Start && time < window.End
            : (window.Day == local.DayOfWeek && time >= window.Start) || (window.Day == previous && time < window.End));
    }

    internal static DateTime CurrentWindowEnd(ReceptionistBusinessConfiguration configuration, DateTime utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        var time = TimeOnly.FromDateTime(local);
        var previous = (DayOfWeek)(((int)local.DayOfWeek + 6) % 7);
        var ends = new List<DateTime>();
        foreach (var window in configuration.Hours)
        {
            DateTime? end = window.Start < window.End
                ? window.Day == local.DayOfWeek && time >= window.Start && time < window.End
                    ? local.Date + window.End.ToTimeSpan() : null
                : window.Day == local.DayOfWeek && time >= window.Start
                    ? local.Date.AddDays(1) + window.End.ToTimeSpan()
                    : window.Day == previous && time < window.End ? local.Date + window.End.ToTimeSpan() : null;
            if (end is null) continue;
            var unspecified = DateTime.SpecifyKind(end.Value, DateTimeKind.Unspecified);
            // A daylight-saving gap is not an implicit extension of an approved window.
            if (zone.IsInvalidTime(unspecified)) return utcNow;
            ends.Add(zone.IsAmbiguousTime(unspecified)
                ? DateTime.SpecifyKind(unspecified - zone.GetAmbiguousTimeOffsets(unspecified).Max(), DateTimeKind.Utc)
                : TimeZoneInfo.ConvertTimeToUtc(unspecified, zone));
        }
        return ends.Count == 0 ? utcNow : ends.Max();
    }

    internal static (int Key, DateTime Start, DateTime End) Month(string zoneId, int year, int month)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var localStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddMonths(1);
        return (checked(year * 100 + month), TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeZoneInfo.ConvertTimeToUtc(localEnd, zone));
    }

    internal static decimal Maximum(decimal fixedAmount, decimal perSecond, int seconds) =>
        Money(decimal.Ceiling(checked((fixedAmount + perSecond * seconds) * 1000000m)) / 1000000m);

    internal static decimal Money(decimal value)
    {
        if (value < 0m || value > 99999999999999.999999m || decimal.Round(value, 6) != value)
            throw new ArgumentException("Money must be nonnegative and representable with six decimal places.");
        return value;
    }

    internal static string Currency(string value)
    {
        if (value is not { Length: 3 } || value.Any(ch => ch is < 'A' or > 'Z'))
            throw new ArgumentException("An explicit uppercase ISO currency code is required.");
        return value;
    }

    internal static string Identifier(string value, int maximum, string name)
    {
        Required(value, maximum, name);
        if (value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/')))
            throw new ArgumentException($"{name} contains unsupported characters.");
        return value;
    }

    internal static string Required(string? value, int maximum, string name, bool allowLines = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            (!allowLines && (value.Any(char.IsControl) || value.Trim() != value)))
            throw new ArgumentException($"{name} is required and must fit its supported length.");
        return value;
    }

    private static void JsonObjectOrArray(string value, string name)
    {
        Required(value, 262144, name, allowLines: true);
        using var json = JsonDocument.Parse(value);
        if (json.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException($"{name} must be a JSON object or array.");
    }
}
