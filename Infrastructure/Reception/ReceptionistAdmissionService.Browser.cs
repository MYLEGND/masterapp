using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

internal sealed record ReceptionistBrowserClaim(bool MayCreate, string Reason, Guid? CallId,
    Guid? CleanupActionId = null, DateTime? CleanupExpiresUtc = null);

public sealed partial class ReceptionistAdmissionService
{
    private const string BrowserCleanupReceipt = "browser-create-cleanup";
    private const string BrowserCleanupReleased = "browser-cleanup-unused";
    private static readonly TimeSpan BrowserCleanupLifetime = TimeSpan.FromSeconds(90);

    internal async Task<ReceptionistIngressBinding?> GetBrowserBindingAsync(MarketingOwnerScope owner, CancellationToken ct)
    {
        await using var db = new MasterAppDbContext(options);
        var ingress = await db.MarketingConnections.AsNoTracking().Where(x => x.OwnerKey == owner.Key &&
            x.Provider == MarketingDestinationKeys.OpenAi && x.ReceptionistAdmissionEnabled).Select(x => x.ReceptionistIngressKey).SingleOrDefaultAsync(ct);
        if (ingress is null) return null;
        var binding = await GetIngressBindingAsync(MarketingDestinationKeys.OpenAi, ingress, ct);
        return binding?.Owner == owner && binding.Binding.Transport == "webrtc" ? binding : null;
    }

    internal async Task<string?> ReadBrowserStatusAsync(Guid callId, Guid creationId, Guid connectionId, string ownerKey, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        var call = await read.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == callId &&
            x.BrowserCreationId == creationId && x.MarketingConnectionId == connectionId &&
            read.MarketingConnections.Any(c => c.Id == connectionId && c.OwnerKey == ownerKey), ct);
        if (call is null) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        if (call.ProviderClosedUtc is not null) return "ended";
        if (call.MustStopUtc <= now || !await AdmissionStillEnabledAsync(read, call, ct)) return "ending";
        return call.State == "accepted" && call.SidebandLeaseToken is { } token && OwnsLease(call, token, now) ? "active" : "connecting";
    }

    // Only a server-validated, expiring website grant may supply creationId. This is
    // not a provider event and never manufactures a signed ingress/session decision.
    internal async Task<ReceptionistBrowserClaim> ReserveBrowserAsync(ReceptionistIngressBinding expected,
        Guid creationId, string offerSha256, bool controlledTest, CancellationToken ct)
    {
        if (creationId == Guid.Empty) throw new ArgumentException("A browser creation identity is required.");
        RequireSha256(offerSha256);
        if (expected.Binding.Transport != "webrtc" || expected.Binding.ControlledTestOnly != controlledTest) return new(false, "transport_unavailable", null);
        await using var read = new MasterAppDbContext(options);
        var ownerAccount = await read.Set<ReceptionistBudgetAccount>().AsNoTracking().SingleOrDefaultAsync(
            x => x.Kind == "owner" && x.ScopeKey == expected.Owner.Key, ct);
        if (ownerAccount is null) return new(false, "accounting_unavailable", null);
        var accountIds = expected.Binding.Costs.Select(x => x.FundingBudgetAccountId).Append(ownerAccount.Id).Distinct().ToArray();
        if (await read.Set<ReceptionistBudgetAccount>().CountAsync(x => accountIds.Contains(x.Id), ct) != accountIds.Length)
            return new(false, "accounting_unavailable", null);
        return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
        {
            var current = await write.MarketingConnections.SingleOrDefaultAsync(x => x.Id == expected.MarketingConnectionId, ct);
            if (current is null || ReceptionistSql.Owner(current) != expected.Owner ||
                current.Provider != expected.Provider || current.ReceptionistActiveConfigurationId != expected.ConfigurationId ||
                current.ReceptionistIngressKey != expected.Binding.IngressKey || current.ReceptionistProviderProjectKey != expected.Binding.ProviderProjectKey)
                return new ReceptionistBrowserClaim(false, "website_binding_changed", null);
            var previous = await write.Set<ReceptionistCall>().AsNoTracking().SingleOrDefaultAsync(x => x.BrowserCreationId == creationId, ct);
            if (previous is not null)
            {
                // Replays can never claim provider I/O, even after an HTTP timeout.
                var same = previous.MarketingConnectionId == current.Id && previous.ConfigurationVersionId == expected.ConfigurationId &&
                    previous.BrowserOfferSha256 == offerSha256;
                return new ReceptionistBrowserClaim(false, same ? "creation_already_claimed" : "creation_scope_mismatch", null);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            var version = await write.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(
                x => x.Id == expected.ConfigurationId && x.MarketingConnectionId == current.Id, ct);
            var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
            var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
            if (binding?.Transport != "webrtc" || binding != expected.Binding &&
                System.Text.Json.JsonSerializer.Serialize(binding, ReceptionistConfigurationCodec.Json) !=
                System.Text.Json.JsonSerializer.Serialize(expected.Binding, ReceptionistConfigurationCodec.Json))
                return new ReceptionistBrowserClaim(false, "website_binding_changed", null);
            var evaluation = await EvaluateAdmissionAsync(write, current, configuration, binding, ownerAccount,
                accountIds, controlledTest, now, ct);
            foreach (var boundary in evaluation.BoundaryCalls) await HoldVoiceAllocationAsync(write, boundary, ct);
            if (evaluation.Reason is { } reason) return new ReceptionistBrowserClaim(false, reason, null);
            var call = AddReservation(write, current, version.Id, configuration, binding, ownerAccount.Id,
                evaluation, null, controlledTest, now);
            call.BrowserCreationId = creationId; call.BrowserOfferSha256 = offerSha256;
            call.State = "uncertain"; call.ProviderAcceptAttemptId = Guid.NewGuid();
            call.ProviderAcceptWorkerId = "browser-create"; call.ProviderAcceptAttemptedUtc = now;
            call.ProviderStateReceipt = "browser-create-claimed:" + call.ProviderAcceptAttemptId.Value.ToString("N");
            var cleanup = new ReceptionistActionIntent
            {
                CallId = call.Id, LogicalActionKey = "provider_stop/1", CapabilityKey = StopCapability,
                RouteKey = "provider.stop", ArgumentsSha256 = ReceptionistConfigurationCodec.Hash(
                    System.Text.Json.JsonSerializer.Serialize(new { call.Id, creationId, call.ProviderProjectKey })),
                ConfirmationReceipt = BrowserCleanupReceipt, WorkerId = "browser-create",
                CreatedUtc = now, UpdatedUtc = now
            };
            write.Add(cleanup);
            return new ReceptionistBrowserClaim(true, "creation_claimed", call.Id, cleanup.Id, now.Add(BrowserCleanupLifetime));
        }, ct);
    }

    // Successful setup releases the unused, precommitted cleanup claim. An ambiguous
    // release can be replayed, but must never be interpreted as permission to hang up.
    internal Task<bool> ReleaseBrowserCleanupAsync(Guid callId, Guid creationId, Guid actionId, CancellationToken ct) =>
        WithCallAsync(callId, async (write, call) =>
        {
            if (call.BrowserCreationId != creationId || call.ProviderSessionId is null) return false;
            var action = await write.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == actionId && x.CallId == callId, ct);
            if (action.ConfirmationReceipt != BrowserCleanupReceipt) return false;
            if (action.State == "rejected" && action.VerificationReceipt == BrowserCleanupReleased) return true;
            if (action.State != "pending" || clock.GetUtcNow().UtcDateTime >= action.CreatedUtc.Add(BrowserCleanupLifetime)) return false;
            action.State = "rejected"; action.VerificationReceipt = BrowserCleanupReleased;
            action.UpdatedUtc = clock.GetUtcNow().UtcDateTime; action.Revision = Guid.NewGuid();
            return true;
        }, ct);

    // The request-abort token must not cancel recording an already observed provider
    // result. Caller uses a separate bounded persistence lifetime after provider I/O.
    internal Task<bool> BindBrowserOutcomeAsync(Guid callId, Guid creationId,
        OpenAiReceptionistBrowserOutcome outcome, bool browserPresent, CancellationToken ct) =>
        WithCallAsync(callId, async (write, call) =>
        {
            if (call.BrowserCreationId != creationId || call.ProviderAcceptAttemptId is null)
                throw new InvalidOperationException("The browser creation attempt does not own this reservation.");
            ReceptionistConfigurationCodec.Required(outcome.Receipt, 512, nameof(outcome.Receipt));
            if (call.ProviderSessionId is not null)
            {
                if (call.ProviderSessionId != outcome.SessionId)
                    throw new InvalidOperationException("A browser attempt cannot replace its provider session.");
                var replayNow = clock.GetUtcNow().UtcDateTime;
                var stillUsable = outcome.Confirmed && browserPresent && call.State == "accepted" &&
                    call.ProviderClosedUtc is null && call.MustStopUtc > replayNow &&
                    await AdmissionStillEnabledAsync(write, call, ct);
                // An ambiguous commit may replay this delegate. Return its committed
                // result, while a disconnected browser still forces timely shutdown.
                if (!stillUsable && call.ProviderClosedUtc is null && call.MustStopUtc > replayNow)
                { call.MustStopUtc = replayNow; call.Revision = Guid.NewGuid(); }
                return stillUsable;
            }
            call.ProviderStateReceipt = outcome.Receipt; call.Revision = Guid.NewGuid();
            if (outcome.SessionId is null) return false; // Full exposure remains reserved, without provider work.
            if (!OpenAiReceptionistProtocol.Token(outcome.SessionId, 200)) throw new ArgumentException("Invalid provider identity.");
            call.ProviderSessionId = outcome.SessionId;
            var now = clock.GetUtcNow().UtcDateTime;
            var usable = outcome.Confirmed && browserPresent && call.MustStopUtc > now &&
                await AdmissionStillEnabledAsync(write, call, ct);
            call.State = usable ? "accepted" : "uncertain";
            if (!usable && call.MustStopUtc > now) call.MustStopUtc = now;
            write.Add(new ReceptionistSessionDecision
            {
                Provider = call.Provider, ProviderProjectKey = call.ProviderProjectKey, ProviderSessionId = outcome.SessionId,
                IngressKey = call.IngressKey, CallId = call.Id, Outcome = "reserved", Reason = "browser_creation_observed", DecidedUtc = now
            });
            return usable;
        }, ct);
}
