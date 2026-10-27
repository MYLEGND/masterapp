using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistOperatorService
{
    public async Task<ReceptionistManagementSummary> ManagementAsync(MarketingOwnerScope owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        await RequireOwnerActorAsync(owner, authorize, ct);
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var connection = await db.MarketingConnections.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.OpenAi, ct);
        if (connection is null) return new(Guid.Empty, [], []);
        var credentials = await db.Set<ReceptionistCredentialVersion>().AsNoTracking().Where(x => x.MarketingConnectionId == connection.Id)
            .OrderByDescending(x => x.CreatedUtc).Take(100).Select(x => new ReceptionistCredentialSummary(x.Id, x.ProviderProjectKey, x.Purpose, x.CreatedUtc, x.RevokedUtc != null)).ToArrayAsync(ct);
        var operations = await db.Set<ReceptionistOperationPackage>().AsNoTracking().Where(x => x.MarketingConnectionId == connection.Id)
            .OrderByDescending(x => x.CreatedUtc).Take(100).ToArrayAsync(ct);
        var canReview = false;
        try { _ = RequireFounder(); canReview = true; } catch (UnauthorizedAccessException) { }
        return new(connection.Revision, credentials, operations.Select(x => new ReceptionistSavedOperationSummary(StoredPrefix + x.Id.ToString("N"),
            x.PayloadSha256, x.Operation, x.RevokedUtc is null ? x.ReviewState : "revoked", x.ExpiresUtc, x.Revision)).ToArray(), canReview, await FundingChoicesAsync(db, connection.Id, canReview, ct), services?.GetService<ReceptionistRuntimeAuthority>() is not null);
    }

    private static async Task<ReceptionistFundingChoice[]> FundingChoicesAsync(MasterAppDbContext db, Guid connectionId,
        bool founder, CancellationToken ct)
    {
        var allowed = new HashSet<Guid>();
        if (!founder)
        {
            var version = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                .Where(x => x.MarketingConnectionId == connectionId && x.AcceptedBindingJson != null)
                .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            if (version is not null)
            {
                var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
                var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, config);
                foreach (var cost in binding?.Costs ?? []) allowed.Add(cost.FundingBudgetAccountId);
            }
        }
        var accounts = await db.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .Where(x => x.Kind == "funding" && (founder || allowed.Contains(x.Id)))
            .OrderBy(x => x.Id).Take(65).ToArrayAsync(ct);
        if (accounts.Length > 64) throw new InvalidOperationException("The funding choices exceed the supported bound.");
        return accounts.Select((x, i) => new ReceptionistFundingChoice(x.Id,
            founder ? x.ScopeKey + " (" + x.Currency + ")" : "Existing voice funding " + (i + 1))).ToArray();
    }

    public async Task<Guid> PrepareForOwnerAsync(MarketingOwnerScope owner, ReceptionistOperationPreparationInput input,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        var actor = await RequireOwnerActorAsync(owner, authorize, ct);
        if (input.Operation is not ("accept_binding" or "set_owner_admission" or "create_acceptance_window" or "revoke_acceptance_window") ||
            input.Evidence is null || input.Evidence.Count is < 1 or > 16 || input.Evidence.Any(x => x is null) ||
            input.Costs is null || input.Costs.Count > 16 || input.Costs.Any(x => x is null))
            throw new ArgumentException("Choose one bounded receptionist setup operation and its evidence.");
        await using var db = new MasterAppDbContext(DatabaseOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var connection = await db.MarketingConnections.SingleOrDefaultAsync(x => x.OwnerKey == owner.Key &&
            x.Provider == MarketingDestinationKeys.OpenAi, ct)
            ?? throw new InvalidOperationException("Save receptionist settings first.");
        if (connection.Revision != input.ExpectedConnectionRevision) throw new DbUpdateConcurrencyException("The setup changed.");
        var version = input.Operation == "accept_binding"
            ? await db.Set<ReceptionistConfigurationVersion>().Where(x => x.MarketingConnectionId == connection.Id)
                .OrderByDescending(x => x.Version).FirstAsync(ct)
            : await db.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.MarketingConnectionId == connection.Id &&
                x.Id == connection.ReceptionistActiveConfigurationId, ct);
        var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var riskMode = input.BudgetPolicyMode == ReceptionistOwnerBudgetPolicy.SupportedMode;
        if (input.BudgetPolicyMode is not (null or "") && !riskMode ||
            input.Operation != "accept_binding" && (riskMode || input.AcceptDelayedBudgetOverrun) ||
            !riskMode && (input.AcceptDelayedBudgetOverrun || input.ObservedProviderMonthlyBudget is not null ||
                input.ProviderBudgetPeriodStartUtc is not null || input.ProviderBudgetPeriodEndUtc is not null || input.BudgetPolicyExpiresUtc is not null ||
                input.Costs.Any(x => x.CashFixedEstimate is not null || x.CashPerSecondEstimate is not null ||
                    x.ConsumptionFixedEstimate is not null || x.ConsumptionPerSecondEstimate is not null)))
            throw new ArgumentException("Choose one explicit budget contract; legacy guarantees and risk estimates cannot be mixed.");
        var now = clock.GetUtcNow().UtcDateTime;
        var evidence = input.Evidence.Where(x => !string.IsNullOrWhiteSpace(x.Reference) || !string.IsNullOrWhiteSpace(x.Sha256) || x.ObservedUtc is not null)
            .Select(x => new ReceptionistOperatorEvidence(x.Kind, x.Reference ?? string.Empty, x.Sha256 ?? string.Empty,
                x.ObservedUtc ?? throw new ArgumentException("Each evidence item needs its observation date."))).ToArray();
        string Receipt(string kind) => evidence.SingleOrDefault(x => x.Kind == kind)?.Reference
            ?? throw new ArgumentException("Supply the evidence for " + kind + ".");
        ReceptionistCredentialVersion? credential = null;
        ReceptionistProviderBinding? binding = null;
        if (input.Operation == "accept_binding")
        {
            credential = await db.Set<ReceptionistCredentialVersion>().SingleOrDefaultAsync(x => x.Id == input.ApiCredentialVersionId &&
                x.MarketingConnectionId == connection.Id && x.Provider == MarketingDestinationKeys.OpenAi &&
                x.Purpose == "voice-api" && x.RevokedUtc == null, ct)
                ?? throw new InvalidOperationException("Choose a saved voice credential for this workspace.");
            var founder = false;
            try { _ = RequireFounder(); founder = true; } catch (UnauthorizedAccessException) { }
            var allowed = (await FundingChoicesAsync(db, connection.Id, founder, ct)).Select(x => x.Id).ToHashSet();
            if (input.Costs.Count == 0 || input.Costs.Any(x => !allowed.Contains(x.FundingAccountId)))
                throw new UnauthorizedAccessException("The proposed funding is not available to this workspace.");
            ReceptionistOwnerBudgetPolicy? budgetPolicy = null;
            if (riskMode)
            {
                if (!input.AcceptDelayedBudgetOverrun || input.MaximumSupportedCallSeconds is not null ||
                    input.Costs.Any(x => x.CashFixedMaximum is not null || x.CashPerSecondMaximum is not null ||
                        x.ConsumptionFixedMaximum is not null || x.ConsumptionPerSecondMaximum is not null))
                    throw new ArgumentException("Explicit owner risk consent is required; leave provider-guarantee fields empty.");
                ReceptionistOperatorEvidence Evidence(string kind) => evidence.SingleOrDefault(x => x.Kind == kind)
                    ?? throw new ArgumentException("Supply the evidence for " + kind + ".");
                var observation = Evidence("provider_budget_observation");
                var consent = Evidence("owner_budget_risk");
                var rates = Evidence("cost_estimates");
                var estimates = input.Costs.Select(x => new ReceptionistBudgetEstimate(x.Key, x.FundingAccountId,
                    x.CashFixedEstimate ?? throw new ArgumentException("Explicit fixed cash estimate is required."),
                    x.CashPerSecondEstimate ?? throw new ArgumentException("Explicit cash rate is required."),
                    x.ConsumptionFixedEstimate ?? throw new ArgumentException("Explicit fixed consumption estimate is required."),
                    x.ConsumptionPerSecondEstimate ?? throw new ArgumentException("Explicit consumption rate is required."),
                    x.ZeroCashJustification ?? "", x.ZeroConsumptionJustification ?? "")).ToArray();
                budgetPolicy = new(ReceptionistOwnerBudgetPolicy.SupportedMode, connection.Id, owner.Key,
                    credential.ProviderProjectKey, version.Id, Guid.NewGuid(), version.ConfigurationSha256,
                    config.Currency, config.MonthlyCashLimit, config.MonthlyConsumptionLimit,
                    input.ObservedProviderMonthlyBudget ?? throw new ArgumentException("Observed provider budget is required."),
                    input.ProviderBudgetPeriodStartUtc ?? throw new ArgumentException("Provider budget period start is required."),
                    input.ProviderBudgetPeriodEndUtc ?? throw new ArgumentException("Provider budget period end is required."),
                    observation.ObservedUtc, observation.Reference, observation.Sha256, actor, now,
                    consent.Reference, consent.Sha256, rates.Reference, rates.Sha256,
                    input.BudgetPolicyExpiresUtc ?? throw new ArgumentException("Explicit policy expiry is required."), estimates);
            }
            // Preserve an established route identity; a new identity is generated only by this authority.
            binding = new ReceptionistProviderBinding
            {
                IngressKey = connection.ReceptionistIngressKey ?? "website-" + Guid.NewGuid().ToString("N"),
                ProviderProjectKey = credential.ProviderProjectKey, Model = input.Model ?? string.Empty, Voice = input.Voice ?? string.Empty,
                BackendMode = "application_services", Transport = "webrtc", ControlledTestOnly = input.ControlledTestOnly,
                CredentialReference = StoredPrefix + credential.Id.ToString("N"), MaximumSupportedCallSeconds = riskMode ? 0 : (input.MaximumSupportedCallSeconds
                    ?? throw new ArgumentException("An explicit verified provider duration is required in guaranteed mode.")),
                ProviderVerificationReceipt = Receipt("provider_setup"), ExclusiveProjectVerificationReceipt = Receipt("exclusive_project"),
                BudgetPolicy = budgetPolicy,
                CostVerificationReceipt = Receipt(riskMode ? "cost_estimates" : "cost_bounds"), DisclosureConfigurationReceipt = Receipt("disclosure"),
                RetentionConfigurationReceipt = Receipt("retention"), WebsiteFallbackReceipt = Receipt("website_fallback"),
                PublicActivationReceipt = input.ControlledTestOnly ? null : Receipt("public_activation"),
                Costs = budgetPolicy?.Estimates.Select(x => x.ToReservation()).ToArray() ?? input.Costs.Select(x => new ReceptionistCostComponent(x.Key, x.FundingAccountId,
                    x.CashFixedMaximum ?? throw new ArgumentException("An explicit guaranteed fixed cash amount is required."),
                    x.CashPerSecondMaximum ?? throw new ArgumentException("An explicit guaranteed cash rate is required."),
                    x.ConsumptionFixedMaximum ?? throw new ArgumentException("An explicit guaranteed fixed consumption amount is required."),
                    x.ConsumptionPerSecondMaximum ?? throw new ArgumentException("An explicit guaranteed consumption rate is required."))).ToArray(),
                Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, Infrastructure.Leads.ReceptionistInquiryAuthority.RouteKey,
                    ReceptionistCapabilityStates.Accepted, Receipt("capability." + ReceptionistCapabilities.CanonicalIntake))]
            };
            ReceptionistConfigurationCodec.ValidateBinding(binding, config);
            _ = ReceptionistRuntimeAuthority.Ready(connection.Provider, config, binding);
        }
        ReceptionistWindowOperation? window = null;
        if (input.Operation is "create_acceptance_window" or "revoke_acceptance_window")
        {
            var accepted = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, config);
            if (accepted?.ControlledTestOnly != true || accepted.ProviderProjectKey != connection.ReceptionistProviderProjectKey ||
                accepted.IngressKey != connection.ReceptionistIngressKey)
                throw new InvalidOperationException("An accepted restricted-test configuration is required.");
            var existing = await db.Set<ReceptionistAcceptanceWindow>().AsNoTracking()
                .Where(x => x.ConfigurationVersionId == version.Id).Take(2).ToArrayAsync(ct);
            if (input.Operation == "create_acceptance_window")
            {
                if (existing.Length != 0) throw new InvalidOperationException("This configuration already has its lifetime test window. Accept a new configuration for another window.");
                if (input.WindowExpiresUtc <= now) throw new ArgumentException("Choose a future expiry.");
                window = new(Guid.NewGuid(), Guid.NewGuid(), accepted.IngressKey,
                    evidence.Single(x => x.Kind == input.Operation).Sha256, actor,
                    input.WindowStartsUtc ?? throw new ArgumentException("Choose the UTC start."),
                    input.WindowExpiresUtc ?? throw new ArgumentException("Choose the UTC expiry."));
            }
            else
            {
                if (existing.Length != 1) throw new InvalidOperationException("Exactly one persisted test window is required.");
                var current = existing[0];
                window = new(current.Id, current.Revision, current.IngressKey, current.RestrictedRouteEvidenceSha256,
                    current.CreatedByActorId, DateTime.SpecifyKind(current.StartsUtc, DateTimeKind.Utc),
                    DateTime.SpecifyKind(current.ExpiresUtc, DateTimeKind.Utc));
            }
        }
        var fundingIds = binding?.Costs.Select(x => x.FundingBudgetAccountId).Distinct().ToArray() ?? [];
        var accounts = await db.Set<ReceptionistBudgetAccount>().AsNoTracking().Where(x =>
            x.Kind == "owner" && x.ScopeKey == owner.Key || fundingIds.Contains(x.Id)).ToArrayAsync(ct);
        if (accounts.Any(x => x.Currency != config.Currency || x.Kind == "owner" && x.TimeZoneId != config.TimeZoneId) ||
            fundingIds.Any(id => accounts.All(x => x.Id != id || x.Kind != "funding")))
            throw new InvalidOperationException("The proposed accounting dimensions do not match this configuration.");
        var package = new ReceptionistOperatorPackage
        {
            Version = 1, Label = window is not null ? "Restricted test window" : binding is null ? "Call permission" : "Website voice connection",
            Operation = input.Operation, ExpiresUtc = now.AddMinutes(30), MarketingConnectionId = connection.Id,
            ExpectedOwnerKey = owner.Key, Provider = connection.Provider,
            ProviderProjectKey = binding?.ProviderProjectKey ?? connection.ReceptionistProviderProjectKey ?? string.Empty,
            ConfigurationId = version.Id, ConfigurationSha256 = version.ConfigurationSha256,
            ExpectedConnectionRevision = connection.Revision, ExpectedAccountRevisions = accounts.ToDictionary(x => x.Id, x => x.Revision),
            FundingScopes = accounts.Where(x => x.Kind == "funding").ToDictionary(x => x.Id, x => x.ScopeKey), Evidence = evidence,
            Binding = binding, ApiCredentialSha256 = credential?.ValueSha256,
            Window = window, Enabled = input.Operation == "set_owner_admission" ? input.Enabled : null, ReceiptReference = binding is null ? Receipt(input.Operation) : string.Empty
        };
        // Validate proposed shape with temporary review metadata, never persist or claim a review.
        (package with { ReviewedByFounderOid = actor, ReviewedUtc = now }).Validate(actor, now);
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json);
        if (json.Length > 262144) throw new ArgumentException("The proposed operation is too large.");
        var row = new ReceptionistOperationPackage
        {
            MarketingConnectionId = connection.Id, ConfigurationVersionId = version.Id, Operation = package.Operation,
            PayloadJson = json, PayloadSha256 = ReceptionistConfigurationCodec.Hash(json), ApiCredentialVersionId = credential?.Id,
            CreatedByActorId = actor, CreatedUtc = now, ExpiresUtc = package.ExpiresUtc
        };
        db.Add(row);
        // Serialize preparation against settings/credential changes without changing the package's expected revision.
        db.Entry(connection).Property(x => x.Revision).IsModified = true;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row.Id;
    }

    public async Task<ReceptionistOperationReview> PreviewPendingForOwnerAsync(MarketingOwnerScope owner, Guid id,
        string expectedHash, Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        await RequireOwnerActorAsync(owner, authorize, ct);
        var actor = RequireFounder();
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var row = await db.Set<ReceptionistOperationPackage>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
            db.MarketingConnections.Any(c => c.Id == x.MarketingConnectionId && c.OwnerKey == owner.Key), ct)
            ?? throw new InvalidOperationException("The scoped operation is unavailable.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (row.ReviewState != "pending" || row.RevokedUtc is not null || row.CreatedUtc > now || row.ExpiresUtc <= now ||
            row.PayloadSha256 != expectedHash || ReceptionistConfigurationCodec.Hash(row.PayloadJson) != expectedHash)
            throw new InvalidOperationException("The exact pending operation changed or expired.");
        var package = JsonSerializer.Deserialize<ReceptionistOperatorPackage>(row.PayloadJson, ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("The operation is invalid.");
        if (package.ExpectedOwnerKey != owner.Key || package.MarketingConnectionId != row.MarketingConnectionId ||
            package.ConfigurationId != row.ConfigurationVersionId || package.Operation != row.Operation ||
            package.ReviewedByFounderOid.Length != 0 || package.ReviewedUtc != default)
            throw new InvalidOperationException("The pending operation has inconsistent ownership or review metadata.");
        RequireSavedBudgetConsentCreator(package, row);
        package = package with { ReviewedByFounderOid = actor, ReviewedUtc = now };
        package.Validate(actor, now);
        var state = await admission.ReadOperatorStateAsync(package, expectedHash, ct);
        state.RequirePackageRevisions(package);
        _ = await PreflightAsync(package, state, false, expectedHash, ct);
        return new(StoredPrefix + id.ToString("N"), expectedHash, package.Label, package.Operation, false,
            ["Review verifies this exact saved operation. It does not itself apply the change or verify live calls."],
            state.Facts.Select(x => new ReceptionistOperationFact(x.Label, x.Value))
                .Concat(package.Evidence.Select(x => new ReceptionistOperationFact("Proposed evidence: " + x.Kind,
                    x.Reference + " · SHA-256 " + x.Sha256 + " · observed " + x.ObservedUtc.ToString("O"))))
                .Concat(package.Binding is { } proposed ? new[] {
                    new ReceptionistOperationFact("Proposed voice", proposed.Model + " / " + proposed.Voice),
                    new ReceptionistOperationFact("Proposed mode", proposed.ControlledTestOnly ? "Restricted testing" : "Public"),
                    new ReceptionistOperationFact("Provider duration guarantee", proposed.BudgetPolicy is null ? proposed.MaximumSupportedCallSeconds.ToString() : "Unknown; application deadline is not provider enforcement") }
                    .Concat(proposed.Costs.Select(x => new ReceptionistOperationFact((proposed.BudgetPolicy is null ? "Proposed maximum cost: " : "Proposed reservation estimate: ") + x.Key,
                        FormattableString.Invariant($"Cash {x.CashFixedMaximum} + {x.CashPerSecondMaximum}/s; consumption {x.ConsumptionFixedMaximum} + {x.ConsumptionPerSecondMaximum}/s")))) : [])
                .ToArray(), null, row.Revision);
    }

    public async Task ReviewForOwnerAsync(MarketingOwnerScope owner, Guid id, Guid expectedRevision, string expectedHash,
        bool accepted, Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        var preview = await PreviewPendingForOwnerAsync(owner, id, expectedHash, authorize, ct);
        if (preview.PendingReviewRevision != expectedRevision) throw new DbUpdateConcurrencyException("The pending review changed.");
        await ReviewSavedOperationAsync(id, expectedRevision, expectedHash, accepted, ct);
    }

    public async Task<Guid> SaveCredentialAsync(MarketingOwnerScope owner, ReceptionistCredentialInput input,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        var actor = await RequireOwnerActorAsync(owner, authorize, ct);
        if (!OpenAiReceptionistProtocol.Token(input.ProviderProjectKey, 200) || !input.ProviderProjectKey.StartsWith("proj_", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(input.Secret) || input.Secret.Length > 16384) throw new ArgumentException("Choose a project and its valid credential.");
        if (input.Purpose == "voice-api") OpenAiReceptionistClient.ValidateCredentials(input.ProviderProjectKey, input.Secret);
        else if (input.Purpose == "voice-webhook") OpenAiReceptionistProtocol.ValidateSigningSecret(input.Secret);
        else throw new ArgumentException("Choose the voice API or webhook credential.");
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var connection = await db.MarketingConnections.SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.OpenAi, ct)
            ?? throw new InvalidOperationException("Save the receptionist business settings first.");
        if (connection.Revision != input.ExpectedConnectionRevision) throw new DbUpdateConcurrencyException("The workspace setup changed. Reload before saving.");
        var credential = new ReceptionistCredentialVersion
        {
            MarketingConnectionId = connection.Id, Provider = MarketingDestinationKeys.OpenAi, ProviderProjectKey = input.ProviderProjectKey,
            Purpose = input.Purpose, ProtectedValue = ReceptionistCredentialEnvelope.Protect(protector, owner, MarketingDestinationKeys.OpenAi,
                input.ProviderProjectKey, input.Purpose, input.Secret), ValueSha256 = ReceptionistConfigurationCodec.Hash(input.Secret),
            CreatedByActorId = actor, CreatedUtc = clock.GetUtcNow().UtcDateTime
        };
        connection.Revision = Guid.NewGuid();
        db.Add(credential); await db.SaveChangesAsync(ct);
        return credential.Id;
    }

    public async Task RevokeCredentialAsync(MarketingOwnerScope owner, Guid credentialId, Guid expectedConnectionRevision,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        await RequireOwnerActorAsync(owner, authorize, ct);
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var connection = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.OpenAi, ct);
        if (connection.Revision != expectedConnectionRevision) throw new DbUpdateConcurrencyException("The workspace setup changed.");
        var credential = await db.Set<ReceptionistCredentialVersion>().SingleOrDefaultAsync(x => x.Id == credentialId && x.MarketingConnectionId == connection.Id, ct)
            ?? throw new InvalidOperationException("The scoped credential is unavailable.");
        if (credential.RevokedUtc is not null) return;
        credential.RevokedUtc = clock.GetUtcNow().UtcDateTime; credential.Revision = Guid.NewGuid(); connection.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        // Revocation stops future binding application. Existing accepted bindings retain
        // their pinned envelopes so callers must separately pause and rotate their operation.
    }

    internal async Task ReviewSavedOperationAsync(Guid id, Guid expectedRevision, string expectedHash, bool accepted, CancellationToken ct)
    {
        var actor = RequireFounder();
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var row = await db.Set<ReceptionistOperationPackage>().SingleAsync(x => x.Id == id, ct);
        if (row.Revision != expectedRevision || row.PayloadSha256 != expectedHash || row.ReviewState != "pending" || row.RevokedUtc is not null ||
            ReceptionistConfigurationCodec.Hash(row.PayloadJson) != expectedHash || row.ExpiresUtc <= clock.GetUtcNow().UtcDateTime)
            throw new DbUpdateConcurrencyException("The exact pending operation changed or expired.");
        var now = clock.GetUtcNow().UtcDateTime;
        var package = JsonSerializer.Deserialize<ReceptionistOperatorPackage>(row.PayloadJson, ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("The operation is invalid.");
        if (package.ReviewedByFounderOid.Length != 0 || package.ReviewedUtc != default) throw new InvalidOperationException("Review identity must be server-derived.");
        RequireSavedBudgetConsentCreator(package, row);
        package = package with { ReviewedByFounderOid = actor, ReviewedUtc = now };
        package.Validate(actor, now);
        if (accepted)
        {
            var state = await admission.ReadOperatorStateAsync(package, expectedHash, ct);
            state.RequirePackageRevisions(package);
            _ = await PreflightAsync(package, state, false, expectedHash, ct);
        }
        row.ReviewState = accepted ? "accepted" : "rejected"; row.ReviewedByActorId = actor; row.ReviewedUtc = now;
        row.ReviewedPayloadSha256 = expectedHash; row.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
    }
}
