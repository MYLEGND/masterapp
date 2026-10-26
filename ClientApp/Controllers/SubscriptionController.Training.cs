using ClientApp.Infrastructure;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace ClientApp.Controllers;

public sealed partial class SubscriptionController
{
    private async Task<ClientSubscription?> ResolveOwnedTrainingAsync(Guid? subscriptionId = null)
    {
        var oid = User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(oid) || Request.Cookies.ContainsKey("impClientProfileId")) return null;
        var profile = await _db.ClientProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
            (x.ExternalIdentityObjectId ?? "").ToLower() == oid || (x.ClientUserId ?? "").ToLower() == oid,
            HttpContext.RequestAborted);
        if (profile is null) return null;
        return await HttpContext.RequestServices.GetRequiredService<ClientIdentityAccessService>()
            .FindTrainingBillingSubscriptionAsync(profile.Id, subscriptionId, HttpContext.RequestAborted);
    }

    [TrainingBillingRoute]
    [HttpGet("/subscription/training")]
    public async Task<IActionResult> Training(Guid? subscriptionId = null)
    {
        var subscription = await ResolveOwnedTrainingAsync(subscriptionId);
        if (subscription is null) return Forbid();
        var offer = await _db.ClientSubscriptionOffers.AsNoTracking().SingleAsync(x => x.Id == subscription.AcceptedOfferId,
            HttpContext.RequestAborted);
        var methods = await _paymentMethodService.ListAsync(subscription.ClientProfileId, HttpContext.RequestAborted);
        var payments = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.ClientSubscriptionId == subscription.Id)
            .OrderByDescending(x => x.CreatedUtc).Take(24).ToListAsync(HttpContext.RequestAborted);
        return View("Training", new TrainingBillingViewModel(subscription, offer, methods, payments,
            _squareOptions.ApplicationId, _squareOptions.LocationId, _squareOptions.Environment.ToString()));
    }

    [TrainingBillingRoute]
    [HttpPost("/subscription/training/{subscriptionId:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelTraining(Guid subscriptionId)
    {
        var subscription = await ResolveOwnedTrainingAsync(subscriptionId);
        if (subscription is null) return Forbid();
        var result = await _billingOrchestrator.CancelClientSubscriptionAsync(new(subscription.Id, true,
            BillingActorType.Client, User.GetCanonicalUserId(), $"training-cancel-{subscription.Id:N}"), HttpContext.RequestAborted);
        return TrainingNotice(subscription.Id, result.SanitizedSummary ?? (result.Success ? "Cancellation recorded." : "Cancellation could not be confirmed."));
    }

    [TrainingBillingRoute]
    [HttpPost("/subscription/training/{subscriptionId:guid}/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryTraining(Guid subscriptionId)
    {
        var subscription = await ResolveOwnedTrainingAsync(subscriptionId);
        if (subscription is null) return Forbid();
        var result = await _billingOrchestrator.RetryClientSubscriptionRenewalAsync(new(subscription.Id,
            BillingActorType.Client, User.GetCanonicalUserId(), $"training-retry-{subscription.Id:N}"), HttpContext.RequestAborted);
        return TrainingNotice(subscription.Id, result.SanitizedSummary ?? "Review your current payment status before retrying.");
    }

    [TrainingBillingRoute]
    [HttpPost("/subscription/training/{subscriptionId:guid}/payment-methods")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTrainingPaymentMethod(Guid subscriptionId, ClientPaymentMethodInput input)
    {
        var subscription = await ResolveOwnedTrainingAsync(subscriptionId);
        if (subscription is null) return Forbid();
        if (!HasCompletePaymentMethodInput(input)) return TrainingNotice(subscription.Id, "Complete the cardholder and billing address fields.");
        var result = await _paymentMethodService.AddAsync(new(subscription.ClientProfileId, subscription.Id,
            input.SourceId, input.CardholderName, BuildBillingAddress(input), input.MakeDefault, input.DisplayName,
            BillingActorType.Client, User.GetCanonicalUserId(), $"training-card-{subscription.Id:N}"), HttpContext.RequestAborted);
        return TrainingNotice(subscription.Id, result.SanitizedSummary ?? "Review your saved payment methods.");
    }

    [TrainingBillingRoute]
    [HttpPost("/subscription/training/{subscriptionId:guid}/payment-methods/{paymentMethodId:guid}/default")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DefaultTrainingPaymentMethod(Guid subscriptionId, Guid paymentMethodId)
    {
        var subscription = await ResolveOwnedTrainingAsync(subscriptionId);
        if (subscription is null) return Forbid();
        var result = await _paymentMethodService.SetDefaultAsync(new(subscription.ClientProfileId, subscription.Id,
            paymentMethodId, BillingActorType.Client, User.GetCanonicalUserId(), $"training-default-{subscription.Id:N}"), HttpContext.RequestAborted);
        return TrainingNotice(subscription.Id, result.SanitizedSummary ?? "Review your default payment method.");
    }

    private IActionResult TrainingNotice(Guid subscriptionId, string notice)
    {
        TempData["TrainingNotice"] = notice;
        return RedirectToAction(nameof(Training), new { subscriptionId });
    }
}
