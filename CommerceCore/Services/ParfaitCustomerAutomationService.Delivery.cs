using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using Shared.Analytics;

namespace ParfaitApp.Services;

public sealed partial class ParfaitCustomerAutomationService
{
    private const string EmailProvider = "MicrosoftGraph";
    private const string EmailChannel = "commerce-email";
    private const int MaxDeliveryAttempts = 3;

    private static string DeliveryEventId(Guid workflowId, string triggerKey) =>
        $"{workflowId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(triggerKey.Trim().ToUpperInvariant())))}";

    private IQueryable<MarketingDestinationDelivery> EmailDeliveries(Guid businessId) =>
        _db.MarketingDestinationDeliveries.Where(row => row.CommerceBusinessId == businessId
            && row.Provider == EmailProvider && row.Channel == EmailChannel);

    private bool CanAttemptDelivery(Guid businessId, Guid workflowId, string triggerKey)
    {
        var eventId = DeliveryEventId(workflowId, triggerKey);
        var receipt = EmailDeliveries(businessId).AsNoTracking().SingleOrDefault(row => row.CanonicalEventId == eventId);
        return receipt is null || (receipt.Status == "retry" && receipt.AttemptCount < MaxDeliveryAttempts
            && receipt.NextAttemptUtc <= DateTime.UtcNow);
    }

    // A purchase and analytics consent are not promotional email permission.
    // The commerce opt-in/withdrawal owner must write this explicit evidence.
    private bool HasPromotionalConsent(Guid businessId, string email)
    {
        var lead = LatestMarketingConsent(businessId, email);
        if (lead is null || !lead.MarketingEmailConsent || lead.IsDeleted || lead.IsInternal
            || lead.SourceCtaKey != "commerce-marketing-opt-in-v1" || string.IsNullOrWhiteSpace(lead.MetadataJson)) return false;
        try
        {
            using var metadata = JsonDocument.Parse(lead.MetadataJson);
            return metadata.RootElement.TryGetProperty("unsubscribeToken", out var token)
                && token.ValueKind == JsonValueKind.String && token.GetString()?.Length == 64;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Invalid commerce email consent evidence. Business:{BusinessId} Lead:{LeadId}", businessId, lead.Id);
            return false;
        }
    }

    public bool TryBeginDispatch(ParfaitAutomationDispatchCandidate candidate)
    {
        try { return TryBeginDispatchCore(candidate); }
        catch (Exception ex) when (IsDispatchClaimCollision(ex))
        {
            // No provider call has occurred. A losing claim must not poison later candidates.
            var eventId = DeliveryEventId(candidate.WorkflowId, candidate.TriggerKey);
            foreach (var entry in _db.ChangeTracker.Entries<MarketingDestinationDelivery>().Where(entry =>
                entry.Entity.CommerceBusinessId == candidate.CommerceBusinessId && entry.Entity.Channel == EmailChannel
                && entry.Entity.CanonicalEventId == eventId).ToList()) entry.State = EntityState.Detached;
            _logger.LogInformation("Another dispatcher owns the email claim. Business:{BusinessId} Workflow:{WorkflowId}", candidate.CommerceBusinessId, candidate.WorkflowId);
            return false;
        }
    }

    private static bool IsDispatchClaimCollision(Exception error)
    {
        // EF's execution strategy can wrap the database error in InvalidOperationException.
        // Match only known claim-contention codes; unexpected storage failures still propagate.
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException
                || current is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 }
                || current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 or 19 })
                return true;
        }
        return false;
    }

    private bool TryBeginDispatchCore(ParfaitAutomationDispatchCandidate candidate)
    {
        lock (StateLock)
        {
            using var transaction = _db.Database.IsRelational()
                ? _db.Database.BeginTransaction(IsolationLevel.Serializable) : null;
            if (candidate.Shipment is not null)
            {
                if (!RevalidateShipmentCandidate(candidate)) { transaction?.Commit(); return false; }
            }
            else
            {
                var store = LoadStoreUnsafe(candidate.CommerceBusinessId);
                var workflow = store.Workflows.SingleOrDefault(row => row.Id == candidate.WorkflowId);
                if (workflow is null || !workflow.IsActive || workflow.EligibleFromUtc is null
                    || workflow.UpdatedUtc.Ticks != candidate.WorkflowUpdatedTicks
                    || !HasPromotionalConsent(candidate.CommerceBusinessId, candidate.ToEmail)) return false;
            }
            var current = GetDueDispatchCandidates(candidate.CommerceBusinessId).SingleOrDefault(row =>
                row.WorkflowId == candidate.WorkflowId && row.TriggerKey == candidate.TriggerKey);
            if (current is null || current.ToEmail != candidate.ToEmail || current.Subject != candidate.Subject
                || current.HtmlBody != candidate.HtmlBody) return false;
            var eventId = DeliveryEventId(candidate.WorkflowId, candidate.TriggerKey);
            var receipt = EmailDeliveries(candidate.CommerceBusinessId).SingleOrDefault(row => row.CanonicalEventId == eventId);
            if (receipt is not null)
            {
                _db.Entry(receipt).Reload();
                if ((receipt.Status != "retry" && !(candidate.Shipment is not null && receipt.Status == "pending"))
                    || receipt.AttemptCount >= MaxDeliveryAttempts || receipt.NextAttemptUtc > DateTime.UtcNow)
                    return false;
            }
            else
            {
                if (candidate.Shipment is not null) return false;
                var owner = MarketingOwnerScope.Business(candidate.CommerceBusinessId);
                receipt = new MarketingDestinationDelivery {
                    OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = candidate.CommerceBusinessId,
                    Provider = EmailProvider, Channel = EmailChannel, CanonicalSource = "CommerceCustomerAutomation",
                    CanonicalEventId = eventId, CanonicalEventName = candidate.TriggerType, ProviderEventName = "SendMail"
                };
                _db.MarketingDestinationDeliveries.Add(receipt);
            }
            receipt.Status = "sending";
            receipt.AttemptCount++;
            receipt.LastAttemptUtc = DateTime.UtcNow;
            receipt.UpdatedUtc = DateTime.UtcNow;
            receipt.ClaimToken = Guid.NewGuid().ToString("N");
            receipt.ClaimExpiresUtc = DateTime.UtcNow.AddMinutes(10);
            receipt.NextAttemptUtc = null;
            receipt.ProviderReceiptJson = JsonSerializer.Serialize(new ParfaitAutomationDispatchLogRecord {
                Shipment = candidate.Shipment,
                WorkflowId = candidate.WorkflowId, WorkflowName = candidate.WorkflowName,
                TriggerType = candidate.TriggerType, TriggerKey = candidate.TriggerKey,
                RecipientEmail = candidate.ToEmail, RecipientFirstName = candidate.FirstName,
                Subject = candidate.Subject, OrderNumber = candidate.OrderNumber, CheckoutAttemptId = candidate.CheckoutAttemptId
            });
            _db.SaveChanges();
            transaction?.Commit();
            candidate.DeliveryId = receipt.Id;
            candidate.DeliveryClaimToken = receipt.ClaimToken;
            return true;
        }
    }

    public void MarkDispatchSent(ParfaitAutomationDispatchCandidate candidate) => CompleteDelivery(candidate, "accepted", null);

    public void MarkDispatchFailed(ParfaitAutomationDispatchCandidate candidate, string errorMessage) =>
        CompleteDelivery(candidate, "uncertain", "Delivery outcome requires review; automatic retry is held.");

    public void MarkDispatchRejected(ParfaitAutomationDispatchCandidate candidate, int statusCode) =>
        CompleteDelivery(candidate, statusCode == 429 ? "retry" : "rejected", $"Email provider rejected the request (HTTP {statusCode}).");

    private void CompleteDelivery(ParfaitAutomationDispatchCandidate candidate, string status, string? error)
    {
        var receipt = EmailDeliveries(candidate.CommerceBusinessId).Single(row => row.Id == candidate.DeliveryId);
        _db.Entry(receipt).Reload();
        if (receipt.Status != "sending" || receipt.ClaimToken != candidate.DeliveryClaimToken)
            throw new InvalidOperationException("The email delivery claim no longer belongs to this attempt.");
        receipt.Status = status == "retry" && receipt.AttemptCount >= MaxDeliveryAttempts ? "rejected" : status;
        receipt.SentUtc = status == "accepted" ? DateTime.UtcNow : null;
        receipt.ErrorMessage = error;
        receipt.NextAttemptUtc = receipt.Status == "retry" ? DateTime.UtcNow.AddMinutes(5 * Math.Pow(3, receipt.AttemptCount - 1)) : null;
        receipt.UpdatedUtc = DateTime.UtcNow;
        receipt.ClaimToken = null;
        receipt.ClaimExpiresUtc = null;
        _db.SaveChanges();
    }

    private List<ParfaitAutomationDispatchLogRecord> ReadDeliveryActivity(Guid businessId)
    {
        // Bound the display only. Durable identities and ambiguous attempts are never pruned here.
        return EmailDeliveries(businessId).AsNoTracking().OrderByDescending(row => row.UpdatedUtc).Take(500).ToList()
            .Select(row => {
                var activity = row.ProviderReceiptJson is null ? new ParfaitAutomationDispatchLogRecord()
                    : JsonSerializer.Deserialize<ParfaitAutomationDispatchLogRecord>(row.ProviderReceiptJson) ?? new();
                activity.Status = row.Status == "sending" && row.ClaimExpiresUtc < DateTime.UtcNow ? "Uncertain"
                    : char.ToUpperInvariant(row.Status[0]) + row.Status[1..];
                activity.ErrorMessage = row.ErrorMessage ?? (activity.Status == "Uncertain" ? "Delivery outcome requires review; automatic retry is held." : "");
                activity.OccurredUtc = row.UpdatedUtc;
                return activity;
            }).ToList();
    }
}
