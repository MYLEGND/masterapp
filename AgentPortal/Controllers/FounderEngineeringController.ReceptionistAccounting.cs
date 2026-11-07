using System.Globalization;
using AgentPortal.Models;
using AgentPortal.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

public sealed partial class FounderEngineeringController
{
    [HttpGet("receptionist/accounting")]
    [ReceptionistMetadataRead]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReceptionistAccounting(string expectedOwnerKey, Guid configurationId, CancellationToken ct)
    {
        FounderGuard.EnsureFounderOrThrow(User); _ = ReceptionistMetadataAccess.Require(HttpContext);
        try { return await ReceptionistPageAsync(ct, accounting: await ReceptionistOperator.PrepareAccountingAsync(expectedOwnerKey, configurationId, ct)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or DbUpdateException or System.Text.Json.JsonException)
        { Response.StatusCode = 400; return await ReceptionistPageAsync(ct, error: "The exact current accounting scope could not be read. Check the owner and operating configuration."); }
    }

    [HttpPost("receptionist/accounting/prepare")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> PrepareSavedAccounting([FromForm] FounderAccountingDraftInput input, CancellationToken ct)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        static bool Utc(string? value, out DateTime date) => DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
        DateTime? cutoff = null;
        if (!string.IsNullOrEmpty(input.ReconciledThroughUtc))
        {
            if (Utc(input.ReconciledThroughUtc, out var parsed)) cutoff = parsed;
            else ModelState.AddModelError(nameof(input.ReconciledThroughUtc), "An exact UTC cutoff is required.");
        }
        if (!ModelState.IsValid || !Utc(input.EvidenceObservedUtc, out var observed))
        { Response.StatusCode = 400; return await ReceptionistPageAsync(ct, error: "Supply the exact current accounting observation and dated evidence. Opening totals must be entered explicitly."); }
        try
        {
            var prepared = await ReceptionistOperator.PrepareSavedAccountingAsync(new(input.ExpectedOwnerKey, input.ConfigurationId,
                input.ExpectedConnectionRevision, input.BudgetAccountId, input.ExpectedAccountRevision, input.Operation,
                input.EvidenceReference, input.EvidenceSha256, observed, input.KnownCashCommitted, input.KnownConsumptionCommitted,
                input.KnownVoiceSeconds, cutoff, input.Enabled), ct);
            return await ReceptionistPageAsync(ct,
                pendingAccountingReview: await ReceptionistOperator.PreviewSavedAccountingAsync(prepared.Id, prepared.Sha256, ct));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or DbUpdateException or System.Text.Json.JsonException or OverflowException)
        { Response.StatusCode = 400; return await ReceptionistPageAsync(ct, error: "The current accounting facts, evidence or admission conditions could not be verified. Read the accounting status again; no accounting change was applied."); }
    }

    [HttpPost("receptionist/accounting/preview-review")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> PreviewSavedAccounting([FromForm] FounderAccountingReviewInput input, CancellationToken ct)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!ModelState.IsValid || input.Id == Guid.Empty)
        { Response.StatusCode = 400; return await ReceptionistPageAsync(ct, error: "Choose an exact pending accounting review."); }
        try { return await ReceptionistPageAsync(ct, pendingAccountingReview: await ReceptionistOperator.PreviewSavedAccountingAsync(input.Id, input.Hash, ct)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or DbUpdateException or System.Text.Json.JsonException)
        { Response.StatusCode = 409; return await ReceptionistPageAsync(ct, error: "The accounting review changed or expired. Read and review current evidence."); }
    }

    [HttpPost("receptionist/accounting/review")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> ReviewSavedAccounting([FromForm] FounderAccountingReviewInput input, CancellationToken ct)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        if (!ModelState.IsValid || input.Id == Guid.Empty || input.Revision == Guid.Empty)
        { Response.StatusCode = 400; return await ReceptionistPageAsync(ct, error: "A current exact accounting preview is required."); }
        try
        {
            await ReceptionistOperator.ReviewSavedAccountingAsync(input.Id, input.Revision, input.Hash, input.Accepted, ct);
            return await ReceptionistPageAsync(ct);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or DbUpdateException or System.Text.Json.JsonException)
        { Response.StatusCode = 409; return await ReceptionistPageAsync(ct, error: "The accounting review changed or expired. Review a fresh observation."); }
    }
}
