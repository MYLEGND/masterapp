using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;

namespace Infrastructure.WebsiteEditing;

// Both app shells create one-use handoffs. Protect alone redeems them into business editor tickets.
public static class WebsiteEditorHandoffService
{
    public static async Task<(string OpaqueState, DateTime ExpiresUtc)> CreateAsync(
        MasterAppDbContext db, Guid clientProfileId, Guid commerceBusinessId,
        string actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty || commerceBusinessId == Guid.Empty || string.IsNullOrWhiteSpace(actorUserId))
            throw new ArgumentException("A complete website editor handoff authority is required.");

        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.AddMinutes(2);
        var token = WebsiteEditorHandoffToken.Create();

        db.ClientIdentityContinuations.Add(new ClientIdentityContinuation
        {
            ClientProfileId = clientProfileId,
            CommerceBusinessId = commerceBusinessId,
            ActorUserId = actorUserId.Trim(),
            ActorEmail = (actorEmail ?? "").Trim().ToLowerInvariant(),
            Purpose = ClientIdentityContinuationPurpose.WebsiteEditor,
            TokenHash = WebsiteEditorHandoffToken.Hash(token),
            IntendedNormalizedEmail = (actorEmail ?? "").Trim().ToLowerInvariant(),
            ReturnUrl = "/",
            ExpiresUtc = expiresUtc,
            CreatedUtc = nowUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        return (token, expiresUtc);
    }

}
