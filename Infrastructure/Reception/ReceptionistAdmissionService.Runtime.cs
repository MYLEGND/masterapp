using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistAdmissionService
{
    /// <summary>A server-observed runtime failure may shorten a call deadline; it cannot enable or extend a call.</summary>
    internal Task RequestRuntimeStopAsync(Guid callId, string reason, CancellationToken ct = default)
    {
        if (reason is not ("backend_unavailable" or "invalid_provider_session" or "protocol_limit" or "transport_lost" or "runtime_disabled" or "accounting_unresolved"))
            throw new ArgumentException("Unsupported runtime stop reason.");
        return WithCallAsync(callId, async (write, call) =>
        {
            if (call.ProviderClosedUtc is not null) return true;
            var now = clock.GetUtcNow().UtcDateTime;
            if (call.MustStopUtc > now) call.MustStopUtc = now;
            call.Revision = Guid.NewGuid();
            if (!await write.Set<ReceptionistActionIntent>().AnyAsync(x => x.CallId == callId && x.LogicalActionKey == "runtime_stop", ct))
                write.Add(new ReceptionistActionIntent
                {
                    CallId = callId, LogicalActionKey = "runtime_stop", CapabilityKey = "__runtime_stop",
                    RouteKey = "runtime.stop.request", State = "verified",
                    ArgumentsSha256 = ReceptionistConfigurationCodec.Hash(reason),
                    ConfirmationReceipt = "runtime:" + reason, ActualRecordReference = "receptionist-call:" + callId.ToString("N"),
                    VerificationReceipt = "runtime_stop_requested:" + now.ToString("O"), CreatedUtc = now, UpdatedUtc = now
                });
            return true;
        }, ct);
    }
}
