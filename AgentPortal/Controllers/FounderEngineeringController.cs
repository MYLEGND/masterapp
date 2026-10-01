using System.Security.Cryptography;
using System.Text;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services.Engineering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
[Route("founder/engineering")]
public sealed class FounderEngineeringController(
    IFounderEngineeringCommandCenterService commandCenter) : Controller
{
    private const string ChatGptStateCookie = "__Host-legend-engineering-chatgpt-state";
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        return View(await commandCenter.GetAsync(cancellationToken));
    }

    [HttpPost("contract")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContract(
        FounderEngineeringContractInput input,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var updated = await commandCenter.SaveAsync(input, cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                $"Operational contract v{updated.Version} is live. New agent contexts are bound to revision {Short(updated.Revision)}.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("contract/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreContract(
        FounderEngineeringRestoreInput input,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var restored = await commandCenter.RestoreAsync(input, cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                $"Revision {Short(input.Revision)} was restored as new live contract v{restored.Version}.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("chatgpt/client")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveChatGptClient(
        FounderEngineeringClientRegistrationInput input,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var registration =
                await commandCenter.SaveClientRegistrationAsync(input, cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                registration.EligibilityConfirmed
                    ? "ChatGPT client registration is saved and verified."
                    : "ChatGPT client registration is saved. Connect ChatGPT to verify the issued client and plan scopes.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("chatgpt/connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectChatGpt(CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var start = await commandCenter.BeginChatGptAuthorizationAsync(cancellationToken);
            Response.Cookies.Append(
                ChatGptStateCookie,
                start.State,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/founder/engineering/chatgpt/callback",
                    MaxAge = TimeSpan.FromMinutes(10),
                    IsEssential = true
                });
            return Redirect(start.AuthorizationUrl);
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("chatgpt/callback")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ChatGptCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery(Name = "iss")] string? responseIssuer,
        [FromQuery(Name = "client_id")] string? callbackClientId,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);

        var cookieState = Request.Cookies[ChatGptStateCookie];
        Response.Cookies.Delete(
            ChatGptStateCookie,
            new CookieOptions
            {
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/founder/engineering/chatgpt/callback"
            });

        if (!SameState(cookieState, state))
        {
            TempData["FounderEngineeringError"] =
                "ChatGPT authorization did not match this browser session. Start Connect again.";
            return RedirectToAction(nameof(Index));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            if (!string.IsNullOrWhiteSpace(state))
                await commandCenter.AbortChatGptAuthorizationAsync(state, cancellationToken);
            TempData["FounderEngineeringError"] =
                error == "access_denied"
                    ? "ChatGPT authorization was cancelled or plan access was not granted."
                    : "ChatGPT authorization was rejected by the provider.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var result = await commandCenter.CompleteChatGptAuthorizationAsync(
                code ?? string.Empty,
                state ?? string.Empty,
                responseIssuer,
                callbackClientId,
                cancellationToken);
            TempData["FounderEngineeringSuccess"] =
                result.Ready
                    ? "ChatGPT Plan connected. LEGEND verified authorization and completed a non-mutating inference readiness canary through the governed Responses route."
                    : "ChatGPT authorization completed, but inference readiness is blocked. The runtime card shows the exact recovery state.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("chatgpt/runtime/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryChatGptRuntime(
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        try
        {
            var result = await commandCenter.RetryChatGptRuntimeAsync(cancellationToken);
            if (result.Ready)
                TempData["FounderEngineeringSuccess"] =
                    "ChatGPT runtime readiness is green after a completed non-mutating inference canary.";
            else
                TempData["FounderEngineeringError"] = ErrorMessage(result.Code);
        }
        catch (InvalidOperationException exception)
        {
            TempData["FounderEngineeringError"] = ErrorMessage(exception.Message);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("chatgpt/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectChatGpt(
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(User);
        await commandCenter.DisconnectChatGptAsync(cancellationToken);
        TempData["FounderEngineeringSuccess"] =
            "ChatGPT-plan authorization was disconnected. Deterministic monitoring continues; no new model execution can start until reconnected.";
        return RedirectToAction(nameof(Index));
    }

    private static bool SameState(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right) ||
            left.Length > 512 ||
            right.Length > 512)
            return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length &&
               CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Short(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Length <= 10 ? value : value[..10];

    private static string ErrorMessage(string code) => code switch
    {
        "engineering_operational_contract_changed" =>
            "The live contract changed before this save completed. Reload and apply your edits to the newest revision.",
        "engineering_contract_too_large" =>
            "The combined operating directives exceed the safe contract size.",
        "engineering_contract_directive_invalid" =>
            "One directive contains unsupported control characters or exceeds its safe size.",
        "engineering_model_binding_invalid" =>
            "One model binding is invalid. Choose Auto or one of the models available to the connected ChatGPT account.",
        "engineering_contract_restore_revision_not_found" =>
            "That historical revision is no longer available.",
        "chatgpt_plan_client_registration_missing" =>
            "Add the OpenAI-issued client registration before connecting ChatGPT.",
        "chatgpt_plan_client_registration_invalid" =>
            "The OpenAI-issued client ID is invalid.",
        "chatgpt_plan_client_authentication_method_invalid" =>
            "Choose the client authentication method supplied by OpenAI.",
        "chatgpt_plan_client_secret_required" =>
            "This client registration requires its OpenAI-issued client secret.",
        "chatgpt_plan_client_secret_invalid" =>
            "The supplied client secret is invalid.",
        "chatgpt_plan_discovery_unavailable" =>
            "OpenAI authorization discovery is temporarily unavailable.",
        "chatgpt_plan_discovery_invalid" =>
            "OpenAI authorization discovery returned an unexpected authority.",
        "chatgpt_plan_redirect_uri_invalid" =>
            "The registered ChatGPT callback configuration is invalid.",
        "chatgpt_plan_authorization_state_invalid" or
        "chatgpt_plan_authorization_state_expired" =>
            "The ChatGPT authorization session expired or was already used. Start Connect again.",
        "chatgpt_plan_authorization_callback_invalid" =>
            "The ChatGPT authorization callback was incomplete.",
        "chatgpt_plan_authorization_client_mismatch" =>
            "The ChatGPT callback returned a different client registration than the one LEGEND started with.",
        "chatgpt_plan_authorization_issuer_mismatch" =>
            "The ChatGPT authorization response issuer did not match OpenAI.",
        "chatgpt_plan_authorization_exchange_failed" =>
            "OpenAI rejected the authorization-code exchange. Verify the issued client registration and callback URI.",
        "chatgpt_plan_authorization_invalid" =>
            "The authorization did not return a valid reusable ChatGPT-plan grant.",
        "chatgpt_plan_usage_scope_missing" =>
            "The connected ChatGPT authorization did not grant all required plan-usage scopes.",
        "chatgpt_plan_id_token_validation_failed" =>
            "LEGEND could not validate the OpenAI identity token.",
        "subscription_sharing_usage_limit_exceeded" =>
            "ChatGPT plan usage is currently exhausted for this app or account. Open ChatGPT Settings → Usage to review the account-specific limit, reset, or credit options; LEGEND will not guess a reset time.",
        "subscription_sharing_usage_unavailable" or
        "subscription_sharing_user_unavailable" or
        "chatgpt_plan_response_temporarily_unavailable" =>
            "ChatGPT is temporarily unavailable. LEGEND preserved the provider evidence and will retry only after the bounded provider backoff is due.",
        "subscription_sharing_user_not_eligible" =>
            "This ChatGPT account is not currently eligible for plan sharing with this app.",
        "subscription_sharing_unsupported_capability" or
        "chatgpt_plan_model_payload_incompatible" or
        "chatgpt_plan_model_binding_unavailable" =>
            "The selected model is unavailable or incompatible with the governed LEGEND role payload. Choose another model or Auto.",
        "subscription_sharing_invalid_subscriber" or
        "chatgpt_plan_reauthorization_required" =>
            "Reconnect ChatGPT with the intended account. LEGEND did not fall back to API billing.",
        "chatgpt_plan_authorization_context_required" =>
            "The OpenAI client or grant authorization context needs attention before plan execution can resume.",
        "chatgpt_plan_readiness_canary_required" or
        "chatgpt_plan_readiness_canary_failed" =>
            "A completed inference readiness canary is required before autonomous model work can start.",
        _ => "The engineering control change was rejected by the canonical authority."
    };
}
