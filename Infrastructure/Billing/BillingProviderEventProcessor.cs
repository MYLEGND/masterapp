using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing.Square;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class BillingProviderEventProcessor : IBillingProviderEventProcessor
{
    private readonly MasterAppDbContext _db;

    public BillingProviderEventProcessor(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<BillingProviderEventProcessResult> ProcessAsync(BillingProviderEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var existing = await _db.BillingProviderEvents
            .FirstOrDefaultAsync(
                x => x.Provider == envelope.Provider &&
                     x.ProviderEnvironment == envelope.ProviderEnvironment &&
                     x.ProviderEventId == envelope.ProviderEventId,
                cancellationToken);

        if (existing is not null)
        {
            return new BillingProviderEventProcessResult(true, null, "Provider event already recorded.", false, existing);
        }

        SquareBillingWebhookEventSummary summary;
        try
        {
            summary = SquareBillingWebhookEventParser.ParseRawPayload(envelope.PayloadJson);
        }
        catch (Exception)
        {
            return new BillingProviderEventProcessResult(
                false,
                "WEBHOOK_PAYLOAD_INVALID",
                "The webhook payload could not be reduced to a safe billing event summary.",
                false);
        }

        var nowUtc = DateTime.UtcNow;
        var isSupportedFamily = summary.Family is not SquareBillingWebhookEventFamily.Unknown;
        var initialStatus = isSupportedFamily
            ? BillingProviderEventProcessingStatus.Deferred
            : BillingProviderEventProcessingStatus.IgnoredUnsupported;
        var eventRecord = new BillingProviderEvent
        {
            Provider = envelope.Provider,
            ProviderEnvironment = envelope.ProviderEnvironment,
            ProviderEventId = envelope.ProviderEventId,
            EventType = envelope.EventType,
            ProviderObjectId = summary.ObjectId ?? envelope.ProviderObjectId,
            ReceivedUtc = envelope.ReceivedUtc,
            SignatureValidatedUtc = nowUtc,
            ProcessingStatus = initialStatus,
            RetryUtc = isSupportedFamily ? nowUtc : null,
            ProcessedUtc = isSupportedFamily ? null : nowUtc,
            SafeErrorCode = isSupportedFamily ? null : "WEBHOOK_EVENT_UNSUPPORTED",
            PayloadHash = BillingIdempotency.Hash(envelope.PayloadJson),
            RetainedPayloadJson = summary.ToSanitizedJson()
        };

        _db.BillingProviderEvents.Add(eventRecord);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var duplicate = await _db.BillingProviderEvents
                .FirstOrDefaultAsync(
                    x => x.Provider == envelope.Provider &&
                         x.ProviderEnvironment == envelope.ProviderEnvironment &&
                         x.ProviderEventId == envelope.ProviderEventId,
                    cancellationToken);

            if (duplicate is not null)
            {
                return new BillingProviderEventProcessResult(true, null, "Provider event already recorded.", false, duplicate);
            }

            throw;
        }

        var summaryMessage = isSupportedFamily
            ? "Provider event stored for deferred reconciliation."
            : "Unsupported provider event recorded and safely ignored.";
        return new BillingProviderEventProcessResult(true, eventRecord.SafeErrorCode, summaryMessage, false, eventRecord);
    }
}
