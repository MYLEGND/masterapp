using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    public async Task<MessagingReactionPreferences?> GetReactionPreferencesAsync(MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken)) return null;
        var profileId = await ReactionActorProfileIdAsync(actor, cancellationToken);
        if (profileId == null) return null;
        var tone = await _db.MobileProfileSettings.AsNoTracking()
            .Where(p => p.ProfileId == profileId && p.ParticipantType == actor.ParticipantType)
            .Select(p => (int?)p.PreferredReactionSkinTone).SingleOrDefaultAsync(cancellationToken);
        return new MessagingReactionPreferences(tone ?? 0);
    }

    public async Task<MessagingOperationResult> SetReactionPreferencesAsync(MessagingActor actor,
        int preferredReactionSkinTone, CancellationToken cancellationToken = default)
    {
        actor = NormalizeActor(actor);
        if (!await IsValidActorAsync(actor, cancellationToken))
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available.");
        if (preferredReactionSkinTone is < 0 or > 5)
            return MessagingOperationResult.Failure("MESSAGING_REACTION_PREFERENCE_INVALID", "Choose a supported reaction skin tone.");
        var profileId = await ReactionActorProfileIdAsync(actor, cancellationToken);
        if (profileId == null)
            return MessagingOperationResult.Failure("MESSAGING_ACTOR_INVALID", "Messaging is not available.");
        var settings = await _db.MobileProfileSettings.SingleOrDefaultAsync(p =>
            p.ProfileId == profileId && p.ParticipantType == actor.ParticipantType, cancellationToken);
        if (settings == null)
        {
            settings = new MobileProfileSettings { ProfileId = profileId.Value, ParticipantType = actor.ParticipantType };
            _db.MobileProfileSettings.Add(settings);
        }
        settings.PreferredReactionSkinTone = preferredReactionSkinTone;
        settings.UpdatedUtc = DateTime.UtcNow;
        AddAudit(actor.UserId, "ReactionPreferencesChanged", null, null, null, null, settings.UpdatedUtc);
        return await SaveOperationAsync("ReactionPreferencesChanged", actor.UserId, Guid.Empty, cancellationToken);
    }
}
