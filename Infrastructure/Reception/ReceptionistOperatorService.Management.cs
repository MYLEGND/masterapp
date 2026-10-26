using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
            x.PayloadSha256, x.Operation, x.RevokedUtc is null ? x.ReviewState : "revoked", x.ExpiresUtc, x.Revision)).ToArray(), canReview);
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
        package = package with { ReviewedByFounderOid = actor, ReviewedUtc = now };
        package.Validate(actor, now);
        var state = await admission.ReadOperatorStateAsync(package, expectedHash, ct);
        state.RequirePackageRevisions(package);
        _ = await PreflightAsync(package, state, false, expectedHash, ct);
        return new(StoredPrefix + id.ToString("N"), expectedHash, package.Label, package.Operation, false,
            ["Review verifies this exact saved operation. It does not itself apply the change or verify live calls."],
            state.Facts.Select(x => new ReceptionistOperationFact(x.Label, x.Value)).ToArray(), null, row.Revision);
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
