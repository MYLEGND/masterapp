using ClientApp.Models;
using ClientApp.Services;
using ClientApp.Services.QuickBooks;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ClientApp.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public class BookKeepingController : Controller
{
    private readonly EffectiveClientContextService _clientContext;
    private readonly IQuickBooksIntegrationService _quickBooks;

    public BookKeepingController(
        EffectiveClientContextService clientContext,
        IQuickBooksIntegrationService quickBooks)
    {
        _clientContext = clientContext;
        _quickBooks = quickBooks;
    }

    private Task<EffectiveClientContext?> GetClientContextAsync()
        => _clientContext.ResolveAsync(User, Request.Cookies);

    private static string ResolveRecordType(ClientProfile profile)
    {
        var raw = profile.CrmNotes;
        if (string.IsNullOrWhiteSpace(raw)) return "Lead";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("recordType", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = (prop.GetString() ?? "").Trim();
                if (value.Equals("Business Client", StringComparison.OrdinalIgnoreCase))
                    return "BusinessClient";
                if (value.Equals("Client", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("BusinessClient", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Lead", StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }
        catch
        {
            // Ignore malformed CRM notes; fail closed below.
        }

        return "Lead";
    }

    private static bool IsBusinessClient(EffectiveClientContext ctx)
        => string.Equals(ResolveRecordType(ctx.Profile), "BusinessClient", StringComparison.OrdinalIgnoreCase);

    private static bool BookKeepingEnabled() => false;

    private async Task<EffectiveClientContext?> RequireBusinessClientAsync()
    {
        var context = await GetClientContextAsync();
        if (context == null || !IsBusinessClient(context))
            return null;
        return context;
    }

    [HttpGet]
    public async Task<IActionResult> Index(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!BookKeepingEnabled()) return NotFound();

        var context = await RequireBusinessClientAsync();
        if (context == null) return Forbid();

        var displayName = $"{context.Profile.FirstName} {context.Profile.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "Business Client";

        var data = await _quickBooks.GetDashboardAsync(context.ClientUserId, forceRefresh, ct);

        var vm = new FinancialCommandCenterVm
        {
            WorkspaceName = displayName,
            IsBusinessClient = true,
            IsQuickBooksConfigured = data.IsConfigured,
            IsConnected = data.IsConnected,
            RealmId = data.RealmId,
            LastSyncedUtc = data.LastSyncedUtc,
            LastSyncStatus = data.LastSyncStatus,
            LastSyncError = data.LastSyncError,
            RevenueMtd = data.RevenueMtd,
            RevenueYtd = data.RevenueYtd,
            ExpensesMtd = data.ExpensesMtd,
            ExpensesYtd = data.ExpensesYtd,
            NetProfitMtd = data.NetProfitMtd,
            NetProfitYtd = data.NetProfitYtd,
            CashPosition = data.CashPosition,
            AccountsCount = data.AccountsCount,
            TopExpenseCategories = data.TopExpenseCategories,
            ProfitTrend = data.ProfitTrend,
            RecentTransactions = data.RecentTransactions
        };

        ViewBag.QuickBooksNotice = TempData["QuickBooksNotice"]?.ToString();
        ViewBag.QuickBooksError = TempData["QuickBooksError"]?.ToString();

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectQuickBooks()
    {
        if (!BookKeepingEnabled()) return NotFound();

        var context = await RequireBusinessClientAsync();
        if (context == null) return Forbid();

        if (!_quickBooks.IsConfigured)
        {
            TempData["QuickBooksError"] = "QuickBooks is not configured yet. Set QuickBooks credentials first.";
            return RedirectToAction(nameof(Index));
        }

        var url = _quickBooks.BuildAuthorizationUrl(context.ClientUserId);
        if (string.IsNullOrWhiteSpace(url))
        {
            TempData["QuickBooksError"] = "Unable to start QuickBooks OAuth flow.";
            return RedirectToAction(nameof(Index));
        }

        return Redirect(url);
    }

    [HttpGet]
    public async Task<IActionResult> QuickBooksCallback(
        string? state,
        string? code,
        string? realmId,
        string? error,
        string? error_description,
        CancellationToken ct = default)
    {
        if (!BookKeepingEnabled()) return NotFound();

        var context = await RequireBusinessClientAsync();
        if (context == null) return Forbid();

        if (!string.IsNullOrWhiteSpace(error))
        {
            TempData["QuickBooksError"] = string.IsNullOrWhiteSpace(error_description)
                ? $"QuickBooks authorization failed: {error}."
                : $"QuickBooks authorization failed: {error_description}";
            return RedirectToAction(nameof(Index));
        }

        var result = await _quickBooks.HandleCallbackAsync(
            context.ClientUserId,
            state,
            code,
            realmId,
            ct);

        if (result.Success)
            TempData["QuickBooksNotice"] = result.Message;
        else
            TempData["QuickBooksError"] = result.Message;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectQuickBooks(CancellationToken ct = default)
    {
        if (!BookKeepingEnabled()) return NotFound();

        var context = await RequireBusinessClientAsync();
        if (context == null) return Forbid();

        await _quickBooks.DisconnectAsync(context.ClientUserId, ct);
        TempData["QuickBooksNotice"] = "QuickBooks connection removed for this business client.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshQuickBooks(CancellationToken ct = default)
    {
        if (!BookKeepingEnabled()) return NotFound();

        var context = await RequireBusinessClientAsync();
        if (context == null) return Forbid();

        await _quickBooks.GetDashboardAsync(context.ClientUserId, forceRefresh: true, ct);
        TempData["QuickBooksNotice"] = "QuickBooks financial snapshot refreshed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult Reports()
        => !BookKeepingEnabled() ? NotFound() : RedirectToAction(nameof(Index));
}
