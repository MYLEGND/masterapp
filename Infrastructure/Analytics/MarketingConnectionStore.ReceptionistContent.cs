using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed partial class MarketingConnectionStore
{
    public async Task<ReceptionistContentPreview> PreviewReceptionistContentAsync(MarketingOwnerScope owner,
        string provider, string authorizedActorId, CancellationToken ct = default)
    {
        ReceptionistConfigurationCodec.Required(authorizedActorId, 450, nameof(authorizedActorId));
        await using var read = new MasterAppDbContext(ReceptionistSql.Options(db));
        var key = MarketingDestinationKeys.Normalize(provider);
        var connection = await read.MarketingConnections.AsNoTracking().SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
        return await ReceptionistContentPreviewAsync(read, owner, connection, authorizedActorId, ct);
    }

    private static async Task<ReceptionistContentPreview> ReceptionistContentPreviewAsync(MasterAppDbContext read,
        MarketingOwnerScope owner, MarketingConnection connection, string actor, CancellationToken ct)
    {
        if (ReceptionistSql.Owner(connection) != owner) throw new InvalidOperationException("The configuration owner does not match.");
        var versions = read.Set<ReceptionistConfigurationVersion>().AsNoTracking().Where(x => x.MarketingConnectionId == connection.Id);
        var current = await versions.OrderByDescending(x => x.Version).FirstAsync(ct);
        var baseline = await versions.SingleOrDefaultAsync(x => x.Id == connection.ReceptionistActiveConfigurationId && x.AcceptedBindingJson != null, ct);
        var recovering = baseline is null;
        baseline ??= await versions.Where(x => x.AcceptedBindingJson != null).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        var history = await versions.OrderByDescending(x => x.Version).Take(20)
            .Select(x => new ReceptionistContentVersion(x.Id, x.Version, x.AcceptedBindingJson != null)).ToArrayAsync(ct);
        var draft = ReceptionistConfigurationCodec.Read(current.ConfigurationJson, current.ConfigurationSha256);
        var prior = baseline is null ? draft : ReceptionistConfigurationCodec.Read(baseline.ConfigurationJson, baseline.ConfigurationSha256);
        string? block = null;
        if (baseline is null) block = "No previously accepted provider configuration exists. Complete initial operator acceptance first.";
        else if (current.AcceptedBindingJson is not null) block = "This version has already been applied. Save an inactive draft before reviewing another change.";
        else
        {
            try
            {
                ReceptionistContentReview.Validate(owner, prior, draft);
                var binding = ReceptionistConfigurationCodec.ReadBinding(baseline.AcceptedBindingJson, prior)
                    ?? throw new InvalidOperationException("The prior binding is unavailable.");
                if (binding.BudgetPolicy is not null)
                    throw new InvalidOperationException("A new configuration requires new owner budget risk acceptance and full operator review.");
                if (connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey)
                    throw new InvalidOperationException("The provider identity changed; full operator review is required.");
                ReceptionistRuntimeAuthority.Ready(connection.Provider, draft, binding);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            { block = "This draft cannot reuse prior acceptance. Check content validation; protected operating, privacy, provider or authority changes require full operator review."; }
        }
        var hash = ReceptionistContentReview.Receipt(owner.Key, actor, connection.Revision, current.Id,
            current.ConfigurationSha256, baseline?.Id ?? Guid.Empty,
            ReceptionistConfigurationCodec.Hash(baseline?.AcceptedBindingJson ?? ""));
        return new(connection.Revision, current.Id, current.Version, baseline?.Id ?? Guid.Empty, baseline?.Version ?? 0,
            hash, draft, prior, block is null, block, history, recovering);
    }

    public async Task<ReceptionistContentApplyResult> ApplyReceptionistContentAsync(MarketingOwnerScope owner,
        string provider, ReceptionistContentApplyInput input, string authorizedActorId, CancellationToken ct = default)
    {
        if (!input.ConfirmContentReview) throw new ArgumentException("Explicit review of disclosure, answers and service routing is required.");
        ReceptionistConfigurationCodec.Required(authorizedActorId, 450, nameof(authorizedActorId));
        ReceptionistConfigurationCodec.Required(input.ReviewHash, 64, nameof(input.ReviewHash));
        if (input.ReviewHash.Length != 64 || input.ReviewHash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("An exact content-review fingerprint is required.");
        var key = MarketingDestinationKeys.Normalize(provider);
        var options = ReceptionistSql.Options(db);
        var accounts = await ReceptionistOwnerAccountIdsAsync(options, owner, ct);
        return await ReceptionistSql.TransactionAsync(options, accounts, async write =>
        {
            // Serialize through the same owner connection as saves/admission. Replayed results do not change its current state.
            await write.MarketingConnections.Where(x => x.OwnerKey == owner.Key && x.Provider == key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Revision, x => x.Revision), ct);
            var connection = await write.MarketingConnections.SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
            if (ReceptionistSql.Owner(connection) != owner) throw new InvalidOperationException("The configuration owner does not match.");
            var active = await write.Set<ReceptionistConfigurationVersion>()
                .Where(x => x.MarketingConnectionId == connection.Id).OrderByDescending(x => x.Version).FirstAsync(ct);
            var activeConfiguration = ReceptionistConfigurationCodec.Read(active.ConfigurationJson, active.ConfigurationSha256);
            var priorCandidates = await write.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                .Where(x => x.MarketingConnectionId == connection.Id && x.CreatedByActorId == authorizedActorId &&
                    x.AcceptedBindingJson != null && x.AcceptedBindingJson.Contains(input.ReviewHash)).ToArrayAsync(ct);
            foreach (var prior in priorCandidates)
            {
                var priorConfiguration = ReceptionistConfigurationCodec.Read(prior.ConfigurationJson, prior.ConfigurationSha256);
                var priorBinding = ReceptionistConfigurationCodec.ReadBinding(prior.AcceptedBindingJson, priorConfiguration);
                if (priorBinding?.ContentReviewHash == input.ReviewHash)
                    return new ReceptionistContentApplyResult(await ReceptionistSnapshotAsync(write, owner, connection, ct),
                        prior.Id, prior.Version, input.ReviewHash, true);
            }
            connection = await ReceptionistConnectionForWriteAsync(write, owner, key, input.ExpectedRevision, ct, mustExist: true);
            var preview = await ReceptionistContentPreviewAsync(write, owner, connection, authorizedActorId, ct);
            if (!preview.CanApply || preview.ReviewHash != input.ReviewHash)
                throw new InvalidOperationException("The reviewed draft or accepted baseline changed. Reload and review the current content.");
            var baseline = await write.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == preview.BaselineId && x.MarketingConnectionId == connection.Id, ct);
            var binding = ReceptionistConfigurationCodec.ReadBinding(baseline.AcceptedBindingJson, preview.Baseline)!;
            var derived = binding with
            {
                OperatorAcceptancePackageSha256 = null,
                ContentReviewHash = preview.ReviewHash,
                ContentSourceConfigurationId = baseline.Id,
                ContentSourceConfigurationSha256 = baseline.ConfigurationSha256,
                ContentSourceBindingSha256 = ReceptionistConfigurationCodec.Hash(baseline.AcceptedBindingJson!),
                ContentSourceOperatorAcceptanceSha256 = binding.OperatorAcceptancePackageSha256 ?? binding.ContentSourceOperatorAcceptanceSha256,
                ContentSourceDisclosureReceipt = binding.DisclosureConfigurationReceipt,
                ContentReviewedConfigurationSha256 = active.ConfigurationSha256,
                DisclosureConfigurationReceipt = "owner-content-review:v1:" + preview.ReviewHash
            };
            ReceptionistConfigurationCodec.ValidateBinding(derived, preview.Draft);
            var applied = new ReceptionistConfigurationVersion
            {
                MarketingConnectionId = connection.Id, Version = checked(active.Version + 1),
                ConfigurationJson = active.ConfigurationJson, ConfigurationSha256 = active.ConfigurationSha256,
                AcceptedBindingJson = JsonSerializer.Serialize(derived, ReceptionistConfigurationCodec.Json),
                CreatedByActorId = authorizedActorId, CreatedUtc = DateTime.UtcNow
            };
            write.Add(applied); await write.SaveChangesAsync(ct);
            connection.ReceptionistActiveConfigurationId = applied.Id;
            connection.ReceptionistAdmissionEnabled = false;
            connection.ReceptionistControlRevision = Guid.NewGuid(); Touch(connection);
            await write.SaveChangesAsync(ct);
            return new ReceptionistContentApplyResult(await ReceptionistSnapshotAsync(write, owner, connection, ct),
                applied.Id, applied.Version, preview.ReviewHash, false);
        }, ct);
    }

    public async Task<ReceptionistConfigurationSnapshot> RestoreReceptionistContentDraftAsync(MarketingOwnerScope owner,
        string provider, ReceptionistContentRollbackInput input, string authorizedActorId, CancellationToken ct = default)
    {
        // Only content is copied; current protected settings and permissions are not rolled back.
        await using var read = new MasterAppDbContext(ReceptionistSql.Options(db));
        var key = MarketingDestinationKeys.Normalize(provider);
        var connection = await read.MarketingConnections.AsNoTracking().SingleAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
        var source = await read.Set<ReceptionistConfigurationVersion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.SourceVersionId && x.MarketingConnectionId == connection.Id, ct)
            ?? throw new ArgumentException("Choose a version belonging to this owner.");
        var snapshot = await ReceptionistSnapshotAsync(read, owner, connection, ct);
        var historical = ReceptionistConfigurationCodec.Read(source.ConfigurationJson, source.ConfigurationSha256);
        var current = snapshot.Configuration ?? throw new InvalidOperationException("Current configuration is required.");
        if (input.RestoreOperatingConfiguration)
        {
            // Restore a reviewable draft, never a historical permission or live binding.
            var accepted = ReceptionistConfigurationCodec.ReadBinding(source.AcceptedBindingJson, historical)
                ?? throw new InvalidOperationException("Choose an independently accepted operating configuration.");
            if (connection.ReceptionistProviderProjectKey != accepted.ProviderProjectKey)
                throw new InvalidOperationException("The accepted provider identity no longer matches.");
            if (historical.BookingPolicy?.Enabled == true)
                throw new InvalidOperationException("Calls-only recovery requires an accepted configuration with booking disabled.");
            ReceptionistRuntimeAuthority.Ready(key, historical, accepted);
            return await SaveReceptionistConfigurationAsync(owner, key, input.ExpectedRevision, historical, authorizedActorId, ct);
        }

        var restored = current with
        {
            BusinessName = historical.BusinessName, ConversationInstructions = historical.ConversationInstructions,
            CompactVoicePolicy = historical.CompactVoicePolicySourceSha256 == ReceptionistConfigurationCodec.Hash(current.BusinessPolicy) ? historical.CompactVoicePolicy : null,
            CompactVoicePolicySourceSha256 = historical.CompactVoicePolicySourceSha256 == ReceptionistConfigurationCodec.Hash(current.BusinessPolicy) ? historical.CompactVoicePolicySourceSha256 : null,
            ApprovedKnowledgeJson = historical.ApprovedKnowledgeJson, ServiceDefinitionsJson = historical.ServiceDefinitionsJson
        };
        // Route changes cannot be silently restored. Explicitly review/edit them in the existing setup form.
        ReceptionistApplicationServices.ValidateConfiguration(restored);
        return await SaveReceptionistConfigurationAsync(owner, key, input.ExpectedRevision, restored, authorizedActorId, ct);
    }
}
