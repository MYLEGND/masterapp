using System.Data;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Shared.Calling;
using Shared.Messaging;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService : ILegendCallingAuthority
{
    // Shared policy, consumed by every platform; no TURN credentials or paid fallback.
    internal static readonly LegendCallPolicy DirectCallPolicy = new(
        ["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"]);

    public async Task<LegendCallResult> ExecuteAsync(string userId, string participantType,
        LegendCallCommand command, CancellationToken cancellationToken)
    {
        var actor = NormalizeActor(new MessagingActor(userId, participantType));
        if (command.DeviceId == Guid.Empty || !await IsValidActorAsync(actor, cancellationToken))
            return new(false, "Calling is unavailable for this account.");
        if (command.SignalData?.Length > 24_000)
            return new(false, "The call signal is too large.");
        if (command.Action is "register-voip" or "unregister-voip")
        {
            if (string.IsNullOrWhiteSpace(command.PushToken) || command.PushToken.Length > 512 ||
                command.PushToken.Any(c => !Uri.IsHexDigit(c)) || command.PushEnvironment is not ("sandbox" or "production"))
                return new(false, "Invalid VoIP device registration.");
            if (command.Action == "register-voip")
                await _notifications.RegisterVoipDeviceAsync(actor, command.PushToken, command.PushEnvironment, cancellationToken);
            else await _notifications.DeactivateVoipDeviceAsync(actor, command.PushToken, cancellationToken);
            return new(true, null);
        }
        try
        {
            if (command.Action is "invite" or "cancel")
            {
                return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = _db.Database.IsRelational()
                        ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
                    var result = await InviteCallAsync(actor, command, cancellationToken);
                    if (transaction != null) await transaction.CommitAsync(cancellationToken);
                    // Notify only after commit: the other device can immediately fetch the saved call.
                    if (result.Succeeded && result.Call?.Status == "ringing")
                    {
                        LegendCallPushWakeup.Notify();
                    }
                    return result;
                });
            }
            return await HandleCallAsync(actor, command, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (command.Action is "heartbeat" or "connected" or "received")
            {
                _db.ChangeTracker.Clear();
                try { return await HandleCallAsync(actor, command, cancellationToken); }
                catch (DbUpdateConcurrencyException) { }
            }
            return new(false, "This call changed on another device. Refresh the call status.");
        }
    }

    private async Task<LegendCallResult> InviteCallAsync(MessagingActor actor, LegendCallCommand command, CancellationToken ct)
    {
        if (command.CallId == null || command.CallId == Guid.Empty || command.ConversationId == null)
            return new(false, "A call and conversation are required.");
        var prior = await _db.LegendCallSessions.SingleOrDefaultAsync(c => c.Id == command.CallId, ct);
        if (prior != null)
        {
            if (!IsSameParticipant(prior.CallerUserId, prior.CallerType, actor.UserId, actor.ParticipantType) || prior.CallerDeviceId != command.DeviceId)
                return new(false, "Call unavailable.");
            if (command.Action == "cancel" && prior.Status is "ringing" or "connecting" or "active")
            {
                prior.Status = "ended";
                await SaveCallAsync(prior, ct);
            }
            return new(true, null, await SnapshotAsync(prior, ct), Policy: DirectCallPolicy);
        }
        var conversation = await (await AuthorizedConversationsQueryAsync(actor, ct)).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == command.ConversationId && !c.IsClosed, ct);
        if (conversation == null || conversation.ConversationType == MessagingConversationTypes.Group)
            return new(false, "Open an available direct conversation to call.");
        var actorIds = await ParticipantUserIdFormsAsync(actor, ct);
        var members = await _db.MessageConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversation.Id && p.IsActive).ToArrayAsync(ct);
        if (members.Length != 2) return new(false, "This participant is unavailable for calling.");
        var other = members.SingleOrDefault(p => !IsCurrentActor(p.UserId, p.ParticipantType, actorIds, actor.ParticipantType));
        if (members.Length != 2 || other == null || !await IsValidActorAsync(new(other.UserId, other.ParticipantType), ct))
            return new(false, "This participant is unavailable for calling.");
        var identities = await _participantIdentities.ResolveIdentitiesAsync(
            [new(actor.UserId, actor.ParticipantType), new(other.UserId, other.ParticipantType)], ct);
        var caller = identities.Values.FirstOrDefault(p => IsSameParticipant(p.UserId, p.ParticipantType, actor.UserId, actor.ParticipantType));
        var callee = identities.Values.FirstOrDefault(p => IsSameParticipant(p.UserId, p.ParticipantType, other.UserId, other.ParticipantType));
        if (caller == null || callee == null) return new(false, "Call identity unavailable.");
        var now = DateTime.UtcNow;
        var otherIds = await ParticipantUserIdFormsAsync(new(other.UserId, other.ParticipantType), ct);
        var busy = await _db.LegendCallSessions.AnyAsync(c => c.ExpiresUtc > now &&
            (c.Status == "ringing" || c.Status == "connecting" || c.Status == "active") &&
            ((actorIds.Contains(c.CallerUserId.ToLower()) && c.CallerType == actor.ParticipantType) ||
             (actorIds.Contains(c.CalleeUserId.ToLower()) && c.CalleeType == actor.ParticipantType) ||
             (otherIds.Contains(c.CallerUserId.ToLower()) && c.CallerType == other.ParticipantType) ||
             (otherIds.Contains(c.CalleeUserId.ToLower()) && c.CalleeType == other.ParticipantType)), ct);
        if (busy && command.Action != "cancel") return new(false, "One of you is already in a call.");
        // Bound repeated ringing without introducing a separate identity/rate-limit store.
        if (await _db.LegendCallSessions.CountAsync(c => c.CallerUserId == actor.UserId && c.CallerType == actor.ParticipantType && c.CreatedUtc > now.AddMinutes(-1), ct) >= 5)
            return new(false, "Please wait before calling again.");
        var call = new LegendCallSession
        {
            Id = command.CallId.Value, ConversationId = conversation.Id,
            CallerUserId = actor.UserId, CallerType = actor.ParticipantType,
            CalleeUserId = other.UserId, CalleeType = other.ParticipantType,
            CallerDeviceId = command.DeviceId, CallerName = caller.DisplayName, CalleeName = callee.DisplayName,
            // An early cancellation creates a terminal record under the same
            // serializable transaction as invite. A later invite cannot resurrect it.
            Status = command.Action == "cancel" ? "ended" : "ringing",
            Video = command.Video, CreatedUtc = now,
            ExpiresUtc = command.Action == "cancel" ? now : now.AddSeconds(DirectCallPolicy.RingSeconds)
        };
        _db.LegendCallSessions.Add(call);
        await _db.SaveChangesAsync(ct);
        await PublishCallAsync(new(await SnapshotAsync(call, ct)), ct);
        return new(true, null, await SnapshotAsync(call, ct), Policy: DirectCallPolicy);
    }

    private async Task<LegendCallResult> HandleCallAsync(MessagingActor actor, LegendCallCommand command, CancellationToken ct)
    {
        var actorIds = await ParticipantUserIdFormsAsync(actor, ct);
        var query = _db.LegendCallSessions.Where(c =>
            (actorIds.Contains(c.CallerUserId.ToLower()) && c.CallerType == actor.ParticipantType) ||
            (actorIds.Contains(c.CalleeUserId.ToLower()) && c.CalleeType == actor.ParticipantType));
        if (command.Action == "sync")
        {
            var now = DateTime.UtcNow;
            var calls = await query.AsNoTracking().Where(c => c.ExpiresUtc > now &&
                (c.Status == "ringing" || c.Status == "connecting" || c.Status == "active"))
                .OrderByDescending(c => c.CreatedUtc).Take(8).ToArrayAsync(ct);
            // Membership/block/privacy checks remain the messaging authority's responsibility.
            var allowed = await (await AuthorizedConversationsQueryAsync(actor, ct)).AsNoTracking()
                .Where(c => !c.IsClosed).Select(c => c.Id).ToArrayAsync(ct);
            var snapshots = new List<LegendCallSnapshot>();
            foreach (var item in calls.Where(c => allowed.Contains(c.ConversationId))) snapshots.Add(await SnapshotAsync(item, ct));
            return new(true, null, ActiveCalls: snapshots.ToArray(), Policy: DirectCallPolicy);
        }
        var call = await query.SingleOrDefaultAsync(c => c.Id == command.CallId, ct);
        if (call == null) return new(false, "Call unavailable.");
        var permitted = await (await AuthorizedConversationsQueryAsync(actor, ct)).AsNoTracking().AnyAsync(c => c.Id == call.ConversationId && !c.IsClosed, ct);
        if (!permitted) return new(false, "Call unavailable.");
        var caller = IsCurrentActor(call.CallerUserId, call.CallerType, actorIds, actor.ParticipantType);
        var ownDevice = caller ? call.CallerDeviceId : call.CalleeDeviceId;
        if (call.ExpiresUtc <= DateTime.UtcNow && call.Status is "ringing" or "connecting" or "active")
        {
            call.Status = call.Status == "ringing" ? "missed" : "ended";
            await SaveCallAsync(call, ct);
        }
        if (command.Action == "get") return new(true, null, await SnapshotAsync(call, ct), Policy: DirectCallPolicy);
        if (call.Status is "ended" or "declined" or "missed")
            return command.Action == "end" ? new(true, null, await SnapshotAsync(call, ct)) : new(false, "This call has ended.", await SnapshotAsync(call, ct));
        if (command.Action == "received")
        {
            // Only an authenticated receiving account may confirm presentation.
            // Receipt never claims the answering-device slot or extends the lease.
            if (caller) return new(false, "Only the recipient can confirm call delivery.");
            if (call.Status == "ringing" && call.ReceivedUtc == null)
            {
                call.ReceivedUtc = DateTime.UtcNow;
                await SaveCallAsync(call, ct);
            }
        }
        else if (command.Action == "accept")
        {
            if (caller || (call.CalleeDeviceId != null && call.CalleeDeviceId != command.DeviceId))
                return new(false, "This call was answered on another device.", await SnapshotAsync(call, ct));
            if (call.Status == "ringing")
            {
                call.ReceivedUtc ??= DateTime.UtcNow;
                call.CalleeDeviceId = command.DeviceId;
                call.Status = "connecting";
                call.ExpiresUtc = DateTime.UtcNow.AddSeconds(60);
                await SaveCallAsync(call, ct);
            }
        }
        else if (command.Action == "decline" && !caller && call.Status == "ringing")
        {
            call.Status = "declined";
            await SaveCallAsync(call, ct);
        }
        else
        {
            if (ownDevice != command.DeviceId) return new(false, "Use the device participating in this call.");
            switch (command.Action)
            {
                case "end": call.Status = "ended"; await SaveCallAsync(call, ct); break;
                case "heartbeat":
                case "connected":
                    if (call.Status == "ringing") return new(false, "The call has not been answered.");
                    if (command.Action == "connected") call.Status = "active";
                    call.ExpiresUtc = DateTime.UtcNow.AddSeconds(90);
                    await SaveCallAsync(call, ct, publish: command.Action == "connected"); break;
                case "signal":
                    if (call.Status == "ringing") return new(false, "The call has not been answered.");
                    var signalWindow = DateTime.UtcNow.AddMinutes(-1);
                    if (await _db.LegendCallSignals.CountAsync(s => s.CallId == call.Id && s.CreatedUtc > signalWindow, ct) >= 1024)
                        return new(false, "Too many call signals. Please call again.");
                    if (command.SignalKind is not ("offer" or "answer" or "candidate" or "restart")) return new(false, "Invalid call signal.");
                    if (command.SignalKind != "restart" && string.IsNullOrWhiteSpace(command.SignalData)) return new(false, "A call signal is required.");
                    if (command.SignalKind == "offer")
                    {
                        if (!caller || command.Epoch != call.Epoch + 1) return new(false, "Refresh the call before negotiating.");
                        call.Epoch = command.Epoch;
                        await SaveCallAsync(call, ct, publish: false);
                    }
                    else if (command.Epoch != call.Epoch || (command.SignalKind == "answer" && caller)) return new(false, "This call signal has expired.");
                    await PublishCallAsync(new(await SnapshotAsync(call, ct), command.SignalKind, command.SignalData, command.DeviceId,
                        caller ? call.CalleeDeviceId : call.CallerDeviceId), ct);
                    break;
                default: return new(false, "Unknown call action.");
            }
        }
        return new(true, null, await SnapshotAsync(call, ct), Policy: DirectCallPolicy);
    }

    private async Task SaveCallAsync(LegendCallSession call, CancellationToken ct, bool publish = true)
    {
        call.Version = Guid.NewGuid();
        if (publish) await PublishCallAsync(new(await SnapshotAsync(call, ct)), ct);
        else await _db.SaveChangesAsync(ct);
    }

    private async Task PublishCallAsync(LegendCallEvent callEvent, CancellationToken ct)
    {
        var call = callEvent.Call;
        var callerIds = call.CallerUserIds ?? [call.CallerUserId];
        var calleeIds = call.CalleeUserIds ?? [call.CalleeUserId];
        var groups = callerIds.Select(id => MessagingHub.GroupName(id, call.CallerType))
            .Concat(calleeIds.Select(id => MessagingHub.GroupName(id, call.CalleeType))).Distinct();
        var now = DateTime.UtcNow;
        var payload = System.Text.Json.JsonSerializer.Serialize(callEvent);
        foreach (var group in groups)
            _db.LegendCallSignals.Add(new LegendCallSignal { CallId = call.Id, RecipientGroup = group,
                Payload = payload, CreatedUtc = now, ExpiresUtc = now.AddSeconds(60) });
        await _db.SaveChangesAsync(ct);
    }

    internal async Task<LegendCallSnapshot> SnapshotAsync(LegendCallSession call, CancellationToken ct)
    {
        var callerIds = await ParticipantUserIdFormsAsync(new(call.CallerUserId, call.CallerType), ct);
        var calleeIds = await ParticipantUserIdFormsAsync(new(call.CalleeUserId, call.CalleeType), ct);
        return CallSnapshot(call) with { CallerUserIds = callerIds, CalleeUserIds = calleeIds };
    }

    internal static LegendCallSnapshot CallSnapshot(LegendCallSession call) => new(
        call.Id, call.ConversationId, call.CallerUserId, call.CallerType, call.CalleeUserId, call.CalleeType,
        call.CallerDeviceId, call.CalleeDeviceId, call.CallerName, call.CalleeName,
        call.Video, call.Status, call.CreatedUtc, call.ExpiresUtc, call.Epoch, ReceivedUtc: call.ReceivedUtc);
}
