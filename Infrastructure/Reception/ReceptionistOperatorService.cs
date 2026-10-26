using System.Security.Cryptography;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Shared.Auth;

namespace Infrastructure.Reception;

/// <summary>Authenticated operator entry; all persistence remains in the existing settings/accounting owners.</summary>
public sealed partial class ReceptionistOperatorService(MarketingConnectionStore store,
    ReceptionistAdmissionService admission, IConfiguration configuration, MarketingCredentialProtector protector,
    IHttpContextAccessor httpContext, TimeProvider clock, IServiceProvider? services = null)
{
    private const string PackageRoot = "Receptionist:OperatorPackages";
    private const string PreviewPurpose = "voice-op-preview";
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);

    public Task<ReceptionistBookingRecoveryPreparation> PrepareBookingRecoveryAsync(string expectedOwnerKey,
        Guid configurationId, Guid? callId, CancellationToken ct = default)
    {
        RequireFounder();
        return admission.ReadBookingRecoveryPreparationAsync(expectedOwnerKey, configurationId, callId, ct);
    }

    public Task<ReceptionistOperatorPreparation> PrepareAsync(string expectedOwnerKey, Guid configurationId,
        CancellationToken ct = default)
    {
        RequireFounder();
        if (configurationId == Guid.Empty || string.IsNullOrWhiteSpace(expectedOwnerKey) || expectedOwnerKey.Length > 450)
            throw new ArgumentException("An exact current configuration and settings owner are required.");
        return admission.ReadOperatorPreparationAsync(expectedOwnerKey, configurationId, ct);
    }

    public Task<IReadOnlyList<ReceptionistOperatorPackageSummary>> CatalogAsync(CancellationToken ct = default)
    {
        var actor = RequireFounder();
        ct.ThrowIfCancellationRequested();
        var result = new List<ReceptionistOperatorPackageSummary>();
        foreach (var entry in configuration.GetSection(PackageRoot).GetChildren().Take(129))
        {
            if (result.Count == 128) throw new InvalidOperationException("The operator package catalog exceeds the supported bound.");
            var hash = entry["Sha256"];
            if (!HashValue(hash) || !OpenAiReceptionistProtocol.Token(entry.Key, 100)) continue;
            try
            {
                var package = ReadPackage(entry.Key, hash!, actor);
                result.Add(new(entry.Key, hash!, package.Label));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException) { }
        }
        return Task.FromResult<IReadOnlyList<ReceptionistOperatorPackageSummary>>(result.OrderBy(x => x.PackageKey, StringComparer.Ordinal).ToArray());
    }

    public Task<ReceptionistOperatorPlan> PlanAsync(string packageKey, string expectedPackageSha256,
        CancellationToken ct = default) => PlanCoreAsync(packageKey, expectedPackageSha256, RequireFounder(), null, null, ct);

    private async Task<ReceptionistOperatorPlan> PlanCoreAsync(string packageKey, string expectedPackageSha256,
        string actor, MarketingOwnerScope? owner, Func<MarketingOwnerScope, CancellationToken, Task<bool>>? authorize, CancellationToken ct)
    {
        var package = await ReadPackageAsync(packageKey, expectedPackageSha256, actor, owner, ct);
        ReceptionistOperatorState state;
        try
        {
            state = await admission.ReadOperatorStateAsync(package, expectedPackageSha256, ct);
            if (state.CompletedReference is null) state.RequirePackageRevisions(package);
            var secretHash = (await PreflightAsync(package, state, prepareBinding: false, expectedPackageSha256, ct)).SecretHash;
            var now = clock.GetUtcNow().UtcDateTime;
            var token = protector.Protect(state.Owner, PreviewPurpose, JsonSerializer.Serialize(new Preview(1, actor,
                state.Owner.Key, packageKey, expectedPackageSha256, state.StateSha256, secretHash, now, now.Add(PreviewLifetime)), ReceptionistConfigurationCodec.Json))
                ?? throw new InvalidOperationException("The operator preview could not be protected.");
            if (token.Length > 4096) throw new InvalidOperationException("The operator preview exceeds its supported bound.");
            return new(packageKey, expectedPackageSha256, package.Label, package.Operation, true, [], state.Facts, token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or
            CryptographicException or DbUpdateConcurrencyException)
        {
            // A package is operator evidence, not an application-generated provider verification response.
            return new(packageKey, expectedPackageSha256, package.Label, package.Operation, false,
                ["The reviewed evidence, private credential inputs or expected canonical state are not ready. Review the scoped package before trying again."],
                [new("Evidence", "Operator-reviewed evidence; external provider verification is not performed by this page.")], null);
        }
    }

    public Task<ReceptionistOperatorReceipt> ApplyAsync(ReceptionistOperatorApplyRequest request, CancellationToken ct = default) =>
        ApplyCoreAsync(request, RequireFounder(), null, null, ct);

    private async Task<ReceptionistOperatorReceipt> ApplyCoreAsync(ReceptionistOperatorApplyRequest request, string actor,
        MarketingOwnerScope? owner, Func<MarketingOwnerScope, CancellationToken, Task<bool>>? authorize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = await ReadPackageAsync(request.PackageKey, request.PackageSha256, actor, owner, ct);
        if (string.IsNullOrWhiteSpace(request.ExpectedStateToken) || request.ExpectedStateToken.Length > 4096)
            throw new ArgumentException("A bounded current operator preview is required.");
        var initial = await admission.ReadOperatorStateAsync(package, request.PackageSha256, ct);
        var tokenJson = protector.Unprotect(initial.Owner, PreviewPurpose, request.ExpectedStateToken)
            ?? throw new InvalidOperationException("The operator preview is unavailable.");
        Preview? preview;
        try { preview = JsonSerializer.Deserialize<Preview>(tokenJson, ReceptionistConfigurationCodec.Json); }
        catch (JsonException) { throw new InvalidOperationException("The operator preview is invalid."); }
        var now = clock.GetUtcNow().UtcDateTime;
        if (preview is null || preview.Version != 1 || preview.ActorOid != actor || preview.OwnerKey != initial.Owner.Key ||
            preview.PackageKey != request.PackageKey || preview.PackageSha256 != request.PackageSha256 ||
            preview.IssuedUtc.Kind != DateTimeKind.Utc || preview.ExpiresUtc.Kind != DateTimeKind.Utc ||
            preview.IssuedUtc > now || preview.ExpiresUtc <= now || preview.ExpiresUtc - preview.IssuedUtc != PreviewLifetime)
            throw new InvalidOperationException("The operator preview is invalid or expired.");
        var preflight = await PreflightAsync(package, initial, prepareBinding: true, request.PackageSha256, ct);
        var outcome = "applied";
        async Task Guard(MasterAppDbContext write)
        {
            var currentActor = owner is null ? RequireFounder() : await RequireOwnerActorAsync(owner, authorize!, ct);
            var currentPackage = await ReadPackageAsync(request.PackageKey, request.PackageSha256, currentActor, owner, ct, write);
            await GuardStoredCredentialsAsync(write, currentPackage, ct);
            var current = await admission.ReadOperatorStateAsync(write, currentPackage, request.PackageSha256, ct);
            if (current.CompletedReference is { } completed)
                throw new ReceptionistOperatorReplayException(completed, current.SatisfiedWithoutReceipt);
            current.RequirePackageRevisions(currentPackage);
            if (current.StateSha256 != preview.StateSha256 || currentActor != preview.ActorOid ||
                (await PreflightAsync(currentPackage, current, false, request.PackageSha256, ct)).SecretHash != preview.SecretInputsSha256)
                throw new DbUpdateConcurrencyException("The operator preview changed. A fresh review is required.");
        }
        try
        {
            if (package.Operation == "reconcile_booking_action")
            {
                if (initial.CompletedReference is { } prior)
                    throw new ReceptionistOperatorReplayException(prior, initial.SatisfiedWithoutReceipt);
                initial.RequirePackageRevisions(package);
                if (initial.StateSha256 != preview.StateSha256 || actor != preview.ActorOid)
                    throw new DbUpdateConcurrencyException("The reviewed booking state changed.");
                var booking = services?.GetRequiredService<Bookings.IReceptionistApplicationBooking>() as Bookings.ReceptionistBookingAuthority
                    ?? throw new InvalidOperationException("The canonical booking recovery implementation is unavailable.");
                var result = await booking.ReconcileRecordedAsync(initial.Owner, package.BookingActionId!.Value,
                    package.ExpectedBookingActionRevision!.Value, Guard, ct);
                if (!result.Verified && result.State != "booking_rejected_before_attempt")
                    return Receipt(request, package, result.State, false, false, null, initial.Facts);
            }
            else if (package.Operation is "accept_binding" or "set_owner_admission")
                await store.ApplyReceptionistOperatorAsync(package, initial, preflight.Binding, actor, Guard, ct);
            else
                await admission.ApplyOperatorAccountingAsync(package, initial, Guard, ct);
        }
        catch (ReceptionistOperatorReplayException replay)
        {
            outcome = replay.SatisfiedWithoutReceipt ? "already_satisfied" : "already_applied";
        }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception error) when (error is DbUpdateException or System.Data.Common.DbException or TimeoutException or OperationCanceledException)
        {
            // The transaction response may have been lost. Only persisted exact evidence can establish completion.
            var recovered = await TryReadBackAsync(package, request.PackageSha256, CancellationToken.None);
            return recovered?.CompletedReference is { } reference
                ? Receipt(request, package, recovered.SatisfiedWithoutReceipt ? "already_satisfied" : "already_applied",
                    !recovered.SatisfiedWithoutReceipt, true, reference, recovered.Facts)
                : Receipt(request, package, "commit_unknown", false, false, null, []);
        }
        var readBack = await TryReadBackAsync(package, request.PackageSha256, CancellationToken.None);
        return Receipt(request, package, outcome, outcome != "already_satisfied", readBack?.CompletedReference is not null,
            readBack?.CompletedReference, readBack?.Facts ?? []);
    }

    private async Task<ReceptionistOperatorState?> TryReadBackAsync(ReceptionistOperatorPackage package, string hash, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await admission.ReadOperatorStateAsync(package, hash, timeout.Token); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or
            DbUpdateException or System.Data.Common.DbException or OperationCanceledException or TimeoutException) { return null; }
    }

    private static ReceptionistOperatorReceipt Receipt(ReceptionistOperatorApplyRequest request, ReceptionistOperatorPackage package,
        string outcome, bool committed, bool verified, string? reference, IReadOnlyList<ReceptionistOperatorFact> facts) =>
        new(request.PackageKey, request.PackageSha256, package.Operation, outcome, committed, verified,
            outcome == "already_satisfied" ? null : reference, facts);

    private async Task<(ReceptionistProviderBinding? Binding, string SecretHash)> PreflightAsync(ReceptionistOperatorPackage package,
        ReceptionistOperatorState state, bool prepareBinding, string packageSha, CancellationToken ct)
    {
        if (state.CompletedReference is not null) return (null, string.Empty);
        if (package.Operation == "accept_binding")
        {
            var binding = package.Binding!;
            ReceptionistConfigurationCodec.ValidateBinding(binding, state.Configuration);
            _ = ReceptionistRuntimeAuthority.Ready(package.Provider, state.Configuration, binding);
            var api = await BootstrapSecretAsync(package, binding.CredentialReference, "voice-api", package.ApiCredentialSha256!, ct);
            var webhook = await BootstrapSecretAsync(package, binding.WebhookSecretReference, "voice-webhook", package.WebhookSecretSha256!, ct);
            OpenAiReceptionistClient.ValidateCredentials(package.ProviderProjectKey, api);
            OpenAiReceptionistProtocol.ValidateSigningSecret(webhook);
            var secretHash = ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
                { binding.CredentialReference, package.ApiCredentialSha256, binding.WebhookSecretReference, package.WebhookSecretSha256 }));
            return (prepareBinding ? binding with
            {
                ProtectedApiCredential = ReceptionistCredentialEnvelope.Protect(protector, state.Owner, package.Provider, package.ProviderProjectKey, "voice-api", api),
                ProtectedWebhookSecret = ReceptionistCredentialEnvelope.Protect(protector, state.Owner, package.Provider, package.ProviderProjectKey, "voice-webhook", webhook),
                OperatorAcceptancePackageSha256 = packageSha
            } : null, secretHash);
        }
        if (package.Operation == "set_owner_admission" && package.Enabled == true)
        {
            var binding = state.PinnedBinding ?? throw new InvalidOperationException("Accepted provider configuration is required.");
            _ = ReceptionistRuntimeAuthority.Ready(package.Provider, state.Configuration, binding);
            var api = ReceptionistCredentialEnvelope.Unprotect(protector, state.Owner, package.Provider, package.ProviderProjectKey,
                "voice-api", binding.ProtectedApiCredential ?? string.Empty);
            var webhook = ReceptionistCredentialEnvelope.Unprotect(protector, state.Owner, package.Provider, package.ProviderProjectKey,
                "voice-webhook", binding.ProtectedWebhookSecret ?? string.Empty);
            OpenAiReceptionistClient.ValidateCredentials(package.ProviderProjectKey, api);
            OpenAiReceptionistProtocol.ValidateSigningSecret(webhook);
        }
        return (null, string.Empty);
    }

    private string BootstrapSecret(ReceptionistOperatorPackage package, string reference, string kind, string expectedHash)
    {
        if (!OpenAiReceptionistProtocol.Token(reference, 100) || !HashValue(expectedHash))
            throw new InvalidOperationException("A reviewed private credential source is required.");
        var source = configuration.GetSection("Receptionist:Secrets:" + reference);
        var secret = source["Value"];
        if (source["OwnerKey"] != package.ExpectedOwnerKey || source["Provider"] != package.Provider ||
            source["ProviderProjectKey"] != package.ProviderProjectKey || source["Kind"] != kind ||
            string.IsNullOrEmpty(secret) || secret.Length > 16_384 || ReceptionistConfigurationCodec.Hash(secret) != expectedHash)
            throw new InvalidOperationException("The private credential source changed or does not match the reviewed owner and project.");
        return secret;
    }

    private string RequireFounder()
    {
        var actor = httpContext.HttpContext?.User;
        var configured = new[] { configuration["FOUNDER_OID"], configuration["Founder:Oid"], configuration["FounderOid"],
            Environment.GetEnvironmentVariable("FOUNDER_OID"), Environment.GetEnvironmentVariable("FounderOid") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (configured.Length != 1 || !FounderAuthority.Evaluate(actor, configured[0], isProduction: true, _ => false))
            throw new UnauthorizedAccessException("Canonical configured Founder authority is required.");
        return actor!.GetCanonicalUserId();
    }

    private ReceptionistOperatorPackage ReadPackage(string key, string hash, string actor)
    {
        if (!OpenAiReceptionistProtocol.Token(key, 100) || !HashValue(hash)) throw new ArgumentException("A reviewed package identity is required.");
        var source = configuration.GetSection(PackageRoot + ":" + key);
        var json = source["Json"];
        if (json is null || json.Length is < 2 or > 262_144 || source["Sha256"] != hash || ReceptionistConfigurationCodec.Hash(json) != hash)
            throw new InvalidOperationException("The reviewed operator package changed or is unavailable.");
        var package = JsonSerializer.Deserialize<ReceptionistOperatorPackage>(json, ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("The operator package is invalid.");
        package.Validate(actor, clock.GetUtcNow().UtcDateTime);
        return package;
    }

    internal static bool HashValue(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record Preview(int Version, string ActorOid, string OwnerKey, string PackageKey, string PackageSha256,
        string StateSha256, string SecretInputsSha256, DateTime IssuedUtc, DateTime ExpiresUtc);
}

internal sealed record ReceptionistOperatorEvidence(string Kind, string Reference, string Sha256, DateTime ObservedUtc);

/// <summary>Only deserialized from the existing private host configuration; never an HTTP input or response DTO.</summary>
internal sealed record ReceptionistOperatorPackage
{
    public int Version { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string ReviewedByFounderOid { get; init; } = string.Empty;
    public DateTime ReviewedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public Guid MarketingConnectionId { get; init; }
    public string ExpectedOwnerKey { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ProviderProjectKey { get; init; } = string.Empty;
    public Guid ConfigurationId { get; init; }
    public string ConfigurationSha256 { get; init; } = string.Empty;
    public Guid ExpectedConnectionRevision { get; init; }
    public Dictionary<Guid, Guid> ExpectedAccountRevisions { get; init; } = [];
    public Dictionary<Guid, string> FundingScopes { get; init; } = [];
    public IReadOnlyList<ReceptionistOperatorEvidence> Evidence { get; init; } = [];
    public string ReceiptReference { get; init; } = string.Empty;
    public ReceptionistProviderBinding? Binding { get; init; }
    public string? ApiCredentialSha256 { get; init; }
    public string? WebhookSecretSha256 { get; init; }
    public ReceptionistBudgetOpening? Opening { get; init; }
    public Guid? BudgetAccountId { get; init; }
    public decimal? CashLimit { get; init; }
    public decimal? ConsumptionLimit { get; init; }
    public bool? Enabled { get; init; }
    public Guid? CallId { get; init; }
    public Guid? ExpectedCallRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BookingActionId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ExpectedBookingActionRevision { get; init; }
    public string? ComponentKey { get; init; }
    public ReceptionistDatedCostObservation? CallCost { get; init; }
    public ReceptionistVerifiedAccountCharge? AccountCharge { get; init; }
    public IReadOnlyList<ReceptionistDatedVoiceUsage>? VoiceAllocations { get; init; }
    public long? FinalVoiceSeconds { get; init; }
    public DateTime? ObservedProviderClosedUtc { get; init; }

    internal void Validate(string actor, DateTime now)
    {
        if (Version != 1 || ReviewedByFounderOid != actor || ReviewedUtc.Kind != DateTimeKind.Utc ||
            ExpiresUtc.Kind != DateTimeKind.Utc || ReviewedUtc > now || ExpiresUtc <= now || ReviewedUtc >= ExpiresUtc ||
            MarketingConnectionId == Guid.Empty || ConfigurationId == Guid.Empty || ExpectedConnectionRevision == Guid.Empty ||
            !ReceptionistOperatorService.HashValue(ConfigurationSha256) || Provider != MarketingDestinationKeys.OpenAi)
            throw new ArgumentException("Explicit current operator review and canonical configuration identities are required.");
        ReceptionistConfigurationCodec.Required(Label, 120, nameof(Label));
        ReceptionistConfigurationCodec.Required(ExpectedOwnerKey, 160, nameof(ExpectedOwnerKey));
        if (!OpenAiReceptionistProtocol.Token(ProviderProjectKey, 200)) throw new ArgumentException("A canonical project identity is required.");
        if (ExpectedAccountRevisions is null || ExpectedAccountRevisions.Count > 64 || ExpectedAccountRevisions.Any(x => x.Key == Guid.Empty || x.Value == Guid.Empty) ||
            FundingScopes is null || FundingScopes.Count > 64 || FundingScopes.Any(x => x.Key == Guid.Empty) ||
            Evidence is null || Evidence.Count is < 1 or > 128 || Evidence.Any(x => x is null) || Evidence.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() != Evidence.Count)
            throw new ArgumentException("Bounded canonical revision and evidence maps are required.");
        foreach (var funding in FundingScopes.Values) ReceptionistConfigurationCodec.Required(funding, 200, "fundingScope");
        foreach (var evidence in Evidence)
        {
            ReceptionistConfigurationCodec.Identifier(evidence.Kind, 160, "evidenceKind");
            ReceptionistConfigurationCodec.Required(evidence.Reference, 512, "evidenceReference");
            if (!ReceptionistOperatorService.HashValue(evidence.Sha256) || evidence.ObservedUtc.Kind != DateTimeKind.Utc || evidence.ObservedUtc > ReviewedUtc)
                throw new ArgumentException("Dated operator-reviewed evidence fingerprints are required.");
        }
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        void Supplied(string key, bool present) { if (present) supplied.Add(key); }
        Supplied("binding", Binding is not null); Supplied("secrets", ApiCredentialSha256 is not null || WebhookSecretSha256 is not null);
        Supplied("opening", Opening is not null); Supplied("budget", BudgetAccountId.HasValue);
        Supplied("limits", CashLimit.HasValue || ConsumptionLimit.HasValue); Supplied("enabled", Enabled.HasValue);
        Supplied("call", CallId.HasValue || ExpectedCallRevision.HasValue); Supplied("component", ComponentKey is not null);
        Supplied("callCost", CallCost is not null); Supplied("accountCharge", AccountCharge is not null);
        Supplied("voice", VoiceAllocations is not null); Supplied("finalVoice", FinalVoiceSeconds.HasValue);
        Supplied("closure", ObservedProviderClosedUtc.HasValue);
        Supplied("bookingAction", BookingActionId.HasValue || ExpectedBookingActionRevision.HasValue);
        string[] allowed = Operation switch
        {
            "accept_binding" => ["binding", "secrets"], "open_budget_period" => ["opening"],
            "approve_budget_ceilings" => ["budget", "limits"], "set_budget_admission" => ["budget", "enabled"],
            "set_owner_admission" => ["enabled"], "reconcile_call_cost" => ["call", "component", "callCost"],
            "reconcile_account_charge" => ["accountCharge"], "reconcile_dated_voice" => ["call", "voice", "finalVoice"],
            "record_provider_closure" => ["call", "closure", "finalVoice"],
            "reconcile_booking_action" => ["call", "bookingAction"],
            _ => throw new ArgumentException("Unsupported operator operation.")
        };
        if (supplied.Except(allowed).Any() || allowed.Where(x => x != "finalVoice").Except(supplied).Any())
            throw new ArgumentException("One exact bounded operator operation is required.");
        if (CallId.HasValue && (CallId == Guid.Empty || ExpectedCallRevision is null || ExpectedCallRevision == Guid.Empty))
            throw new ArgumentException("An exact call and expected revision are required.");
        if (Operation == "reconcile_booking_action" && (BookingActionId is null || BookingActionId == Guid.Empty ||
            ExpectedBookingActionRevision is null || ExpectedBookingActionRevision == Guid.Empty))
            throw new ArgumentException("An exact booking action and expected revision are required.");
        if (BudgetAccountId == Guid.Empty || FinalVoiceSeconds < 0) throw new ArgumentException("An exact accounting target is required.");
        if (Operation == "accept_binding")
        {
            if (Binding!.Capabilities is null || Binding.Capabilities.Count > 100 || Binding.Capabilities.Any(x => x is null))
                throw new ArgumentException("A bounded explicit capability map is required.");
            if (Binding!.ProviderProjectKey != ProviderProjectKey || ReceptionistConfigurationCodec.HasContentProvenance(Binding) || Binding.ProtectedApiCredential is not null ||
                Binding.ProtectedWebhookSecret is not null || Binding.OperatorAcceptancePackageSha256 is not null ||
                !ReceptionistOperatorService.HashValue(ApiCredentialSha256) || !ReceptionistOperatorService.HashValue(WebhookSecretSha256))
                throw new ArgumentException("Reviewed raw-source fingerprints and an unsealed private binding are required.");
            RequireEvidence("provider_setup", Binding.ProviderVerificationReceipt);
            RequireEvidence("exclusive_project", Binding.ExclusiveProjectVerificationReceipt);
            RequireEvidence("cost_bounds", Binding.CostVerificationReceipt);
            RequireEvidence("disclosure", Binding.DisclosureConfigurationReceipt);
            RequireEvidence("retention", Binding.RetentionConfigurationReceipt);
            RequireEvidence("carrier_fallback", Binding.CarrierFallbackReceipt);
            foreach (var capability in (Binding.Capabilities ?? []).Where(x => x.State == ReceptionistCapabilityStates.Accepted))
                RequireEvidence("capability." + capability.Key, capability.AcceptanceReceipt);
            if (!Binding.ControlledTestOnly) RequireEvidence("public_activation", Binding.PublicActivationReceipt ?? string.Empty);
        }
        else
        {
            ReceptionistConfigurationCodec.Required(ReceiptReference, 200, nameof(ReceiptReference));
            RequireEvidence(Operation, ReceiptReference);
            if (Opening is not null && Opening.ReconciliationReceipt != ReceiptReference || AccountCharge is not null && AccountCharge.ProviderReceiptKey != ReceiptReference)
                throw new ArgumentException("The command receipt must match its reviewed evidence.");
        }
        if (CashLimit is { } cash) ReceptionistConfigurationCodec.Money(cash);
        if (ConsumptionLimit is { } consumption) ReceptionistConfigurationCodec.Money(consumption);
        if (Operation == "approve_budget_ceilings" && (CashLimit is null || ConsumptionLimit is null))
            throw new ArgumentException("Separate cash and consumption ceilings are required.");
        if (Opening is { } opening)
        {
            ReceptionistConfigurationCodec.Required(opening.ScopeKey, 200, "openingScope");
            ReceptionistConfigurationCodec.Currency(opening.Currency);
            var month = ReceptionistConfigurationCodec.Month(opening.TimeZoneId, opening.Year, opening.Month);
            ReceptionistConfigurationCodec.Money(opening.CashLimit); ReceptionistConfigurationCodec.Money(opening.ConsumptionLimit);
            ReceptionistConfigurationCodec.Money(opening.KnownCashCommitted); ReceptionistConfigurationCodec.Money(opening.KnownConsumptionCommitted);
            if (opening.Kind is not ("owner" or "funding") || opening.KnownVoiceSeconds < 0 || opening.ReconciledThroughUtc.Kind != DateTimeKind.Utc ||
                opening.ReconciledThroughUtc < month.Start || opening.ReconciledThroughUtc >= month.End || opening.ReconciledThroughUtc > now)
                throw new ArgumentException("An explicit dated opening and separate accounting facts are required.");
        }
        void DatedAmount(DateTime? date, decimal amount)
        {
            ReceptionistConfigurationCodec.Money(amount);
            if (amount > 0 && date is null || date is { } actual && (actual.Kind != DateTimeKind.Utc || actual > ReviewedUtc))
                throw new ArgumentException("An actual cash debit or consumption date is required.");
        }
        if (CallCost is { } cost)
        {
            ReceptionistConfigurationCodec.Identifier(ComponentKey!, 100, "component");
            DatedAmount(cost.CashDebitedUtc, cost.CumulativeCashForPeriod); DatedAmount(cost.ConsumptionIncurredUtc, cost.CumulativeConsumptionForPeriod);
            if (cost.FinalCashTotal is { } finalCash) ReceptionistConfigurationCodec.Money(finalCash);
            if (cost.FinalConsumptionTotal is { } finalConsumption) ReceptionistConfigurationCodec.Money(finalConsumption);
        }
        if (AccountCharge is { } charge)
        {
            if (charge.OwnerBudgetAccountId == Guid.Empty || charge.FundingBudgetAccountId == Guid.Empty || charge.OwnerBudgetAccountId == charge.FundingBudgetAccountId)
                throw new ArgumentException("Distinct canonical charge dimensions are required.");
            ReceptionistConfigurationCodec.Required(charge.ProviderChargeKey, 200, "providerCharge");
            ReceptionistConfigurationCodec.Identifier(charge.ComponentKey, 100, "component");
            DatedAmount(charge.CashDebitedUtc, charge.CumulativeCash); DatedAmount(charge.ConsumptionIncurredUtc, charge.CumulativeConsumption);
            if (charge.CashDebitedUtc is null && charge.ConsumptionIncurredUtc is null && !(charge.CashFinal && charge.ConsumptionFinal))
                throw new ArgumentException("A dated charge observation or verified zero final totals are required.");
        }
        if (VoiceAllocations is { } voice && (voice.Count is < 1 or > 256 || voice.Any(x => x is null || x.CumulativeSecondsForPeriod < 0 ||
            x.ServicePeriodEvidenceUtc.Kind != DateTimeKind.Utc || x.ServicePeriodEvidenceUtc > ReviewedUtc)))
            throw new ArgumentException("Bounded verified dated voice allocations are required.");
        if (ObservedProviderClosedUtc is { } closed && (closed.Kind != DateTimeKind.Utc || closed > ReviewedUtc))
            throw new ArgumentException("Verified UTC provider closure evidence is required.");
    }

    private void RequireEvidence(string kind, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Evidence.Any(x => x.Kind == kind && x.Reference == reference))
            throw new ArgumentException("Every accepted fact must name its scoped operator-reviewed evidence.");
    }
}

internal sealed class ReceptionistOperatorReplayException(string reference, bool satisfiedWithoutReceipt) : Exception
{
    internal string Reference { get; } = reference;
    internal bool SatisfiedWithoutReceipt { get; } = satisfiedWithoutReceipt;
}
