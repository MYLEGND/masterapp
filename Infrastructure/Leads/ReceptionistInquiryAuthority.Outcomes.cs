using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Leads;

internal sealed partial class ReceptionistInquiryAuthority
{
    private const string QaReferencePrefix = "receptionist-qa:";
    private const string QaReceiptPrefix = "qa-intake-outcome:v1:";

    /// <summary>
    /// A durable synthetic action outcome in the existing action journal. It never
    /// calls the customer writer or stores transcript/name/contact values. The
    /// separately accepted relational fixtures prove genuine CRM field persistence.
    /// </summary>
    internal async Task<ReceptionistInquiryPersistence> PersistQaOutcomeAsync(
        ReceptionistCallContext supplied, Guid actionId, ConfirmedReceptionistInquiry facts,
        string confirmationReceipt, CancellationToken ct = default)
    {
        facts = ValidateFacts(facts);
        var current = await admission.GetCallContextAsync(supplied.CallId, ct)
            ?? throw new InvalidOperationException("The QA call is absent.");
        RequireSameCall(supplied, current);
        if (!current.ControlledTest)
            throw new InvalidOperationException("Only a trusted controlled test can produce a QA result.");
        var rules = ReadIntakeRules(current);
        if (facts.ServiceKey is null || facts.RequestSummary is null ||
            !rules.Services.Any(x => x.ServiceKey == facts.ServiceKey))
            throw new InvalidOperationException("The synthetic request needs a configured service and confirmed need.");
        await using var read = new MasterAppDbContext(options);
        if (!read.Database.IsRelational())
            throw new InvalidOperationException("QA outcomes require the canonical durable action journal.");
        var action = await read.Set<ReceptionistActionIntent>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actionId && x.CallId == current.CallId, ct);
        if (action is null || !IsInquiryAction(action) ||
            action.ArgumentsSha256 != CanonicalArgumentsSha256(facts) ||
            action.ConfirmationReceipt != confirmationReceipt)
            throw new InvalidOperationException("The synthetic outcome does not match its confirmed, fenced action.");
        var reference = QaReferencePrefix + action.Id.ToString("N");
        var receipt = QaReceipt(current, action);
        if (action.State == "verified")
        {
            if (action.ActualRecordReference != reference || action.VerificationReceipt != receipt)
                throw new InvalidOperationException("The existing QA outcome is inconsistent.");
            return new("qa_outcome_saved", null, reference, false, "qa_test", ExcludedCounting());
        }
        if (action.State != "pending")
            throw new InvalidOperationException("An uncertain or rejected QA action requires readback, not another write.");
        var changed = await admission.RecordSyntheticIntakeOutcomeAsync(current.CallId, action.Id, action.ArgumentsSha256,
            confirmationReceipt, reference, receipt, ct);
        return new("qa_outcome_saved", null, reference, changed, "qa_test", ExcludedCounting());
    }

    /// <summary>Fresh trusted verification reduces the result before returning to the dialogue boundary.</summary>
    internal async Task<ReceptionistApplicationOutcome?> ReadSafeOutcomeAsync(
        ReceptionistCallContext supplied, Guid actionId, CancellationToken ct = default)
    {
        var current = await admission.GetCallContextAsync(supplied.CallId, ct);
        if (current is null) return null;
        RequireSameCall(supplied, current);
        if (!current.ControlledTest)
        {
            var verified = await ReadBackAsync(current.Owner, current.CallId, actionId, ct);
            if (verified is null) return null;
            // Internal CRM contact readback remains inside this deterministic authority.
            return new(verified.OwnerKey, verified.CallId, verified.ActionId, verified.State, false,
                verified.RecordReference, verified.VerificationReceipt);
        }
        _ = ReadIntakeRules(current);
        await using var read = new MasterAppDbContext(options);
        var action = await read.Set<ReceptionistActionIntent>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actionId && x.CallId == current.CallId, ct);
        if (action is null || !IsInquiryAction(action) || action.State != "verified") return null;
        var reference = QaReferencePrefix + action.Id.ToString("N");
        var receipt = QaReceipt(current, action);
        if (action.ActualRecordReference != reference || action.VerificationReceipt != receipt)
            throw new InvalidOperationException("The committed synthetic action readback is inconsistent.");
        return new(current.Owner.Key, current.CallId, action.Id, "qa_outcome_verified", true, reference, receipt);
    }

    private static bool IsInquiryAction(ReceptionistActionIntent action) =>
        action.CapabilityKey == Shared.Analytics.ReceptionistCapabilities.CanonicalIntake &&
        action.RouteKey == RouteKey && HasClaimEvidence(action);

    private static ReceptionistInquiryCounting ExcludedCounting() => new(false, false, "not_required_excluded", 0, 0);

    internal static bool VerifiedQaOutcome(ReceptionistQaScope scope, ReceptionistActionIntent action) =>
        action.CallId == scope.CallId && IsInquiryAction(action) && action.State == "verified" &&
        action.ActualRecordReference == QaReferencePrefix + action.Id.ToString("N") &&
        action.VerificationReceipt == QaReceipt(scope, action);
    private static string QaReceipt(ReceptionistCallContext call, ReceptionistActionIntent action) =>
        QaReceipt(new ReceptionistQaScope(call.Owner.Key, call.CallId, call.ConfigurationId, call.Provider, call.ProviderProjectKey, call.ProviderSessionId), action);
    private static string QaReceipt(ReceptionistQaScope call, ReceptionistActionIntent action) =>
        QaReceiptPrefix + Hash(JsonSerializer.Serialize(new
        {
            kind = "synthetic_intake_outcome", call.Key, call.CallId, call.ConfigurationId,
            call.Provider, call.ProviderProjectKey, call.ProviderSessionId,
            action.Id, action.ArgumentsSha256, action.ConfirmationReceipt,
            action.ExecutionFence, action.ExecutionLeaseToken, action.AttemptId,
            action.CreatedUtc, genuineRecords = 0, outreach = 0
        }, Json));
}

internal sealed record ReceptionistQaScope(string Key, Guid CallId, Guid ConfigurationId,
    string Provider, string ProviderProjectKey, string ProviderSessionId);
