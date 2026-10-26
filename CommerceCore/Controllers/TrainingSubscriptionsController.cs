using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Security;
using Infrastructure.WebsiteRuntime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[Route("store/training")]
public sealed class TrainingSubscriptionsController(CommerceStoreContextService stores, MasterAppDbContext db,
    TrainingEnrollmentService enrollment, IConfiguration configuration) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var store = await stores.ResolvePublicAsync(HttpContext, null, ct);
        if (store is null || !store.IsParfait) return NotFound();
        ViewData["CommerceStoreContext"] = store;
        ViewData["TrainingPreview"] = IsPreviewOnly();
        var plans = await db.CommerceProducts.AsNoTracking().Where(x => x.CommerceBusinessId == store.CommerceBusinessId &&
            x.IsActive && x.BillingKind == ClientSubscriptionScope.MonthlyTraining).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return View("~/Views/TrainingSubscriptions/Index.cshtml", plans);
    }

    [HttpPost("request")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(PlatformRateLimiting.PublicFormPolicy)]
    public async Task<IActionResult> RequestInvitation(Guid productId, string email, string? promotionCode, bool emailConsent, CancellationToken ct)
    {
        if (IsPreviewOnly()) return StatusCode(405);
        var store = await stores.ResolvePublicAsync(HttpContext, null, ct);
        if (store is null || !store.IsParfait) return NotFound();
        if (!emailConsent || string.IsNullOrWhiteSpace(email)) return BadRequest("Consent and a valid email address are required.");
        try
        {
            await enrollment.RequestAsync(store.CommerceBusinessId, productId, email, promotionCode, ct);
        }
        catch (ArgumentException) { return BadRequest("Check your email address, plan and promotion code."); }
        catch (InvalidOperationException) { return StatusCode(503, "Enrollment is temporarily unavailable. No payment has been taken. Please try again later."); }
        catch (DbUpdateException) { return StatusCode(409, "A request is already being processed. Check your email before trying again."); }
        // Never return a token, profile identity or account-existence information.
        TempData["TrainingRequestNotice"] = "If this request is eligible, an activation email will be sent. Check your email for an existing invitation. No payment has been taken.";
        return RedirectToAction(nameof(Index));
    }

    private bool IsPreviewOnly() =>
        !Infrastructure.Billing.ClientSubscriptionActivationPolicyOptions.FromConfiguration(configuration).TrainingSubscriptionsEnabled ||
        (CommerceSharedHostPreviewGate.IsConfigured(configuration) && !CommerceSharedHostCutoverGate.IsConfigured(configuration));
}
