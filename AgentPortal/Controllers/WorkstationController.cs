using System.Linq;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Route("Workstation")]
[Route("Scripts")] // legacy alias; keep routes working for old links
public class WorkstationController : Controller
{
    private readonly ILogger<WorkstationController> _logger;

    public WorkstationController(ILogger<WorkstationController> logger)
    {
        _logger = logger;
    }

    // Landing: default to Life Insurance rebuttals
    [HttpGet("")]
    [HttpGet("Index")]
    public IActionResult Index()
    {
        return RedirectToAction(nameof(LifeInsuranceRebuttals));
    }

    [HttpGet("Queue")]
    public IActionResult Queue([FromQuery] string? queueKey)
    {
        var normalizedQueue = WorkstationLeadBuckets.NormalizeBucket(queueKey) ?? WorkstationLeadBuckets.LifeInsurance;

        if (string.Equals(normalizedQueue, WorkstationLeadBuckets.MortgageProtection, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Rebuttals));

        if (WorkstationLeadBuckets.LifeWorkstationQueueBuckets.Contains(normalizedQueue, StringComparer.OrdinalIgnoreCase))
            return RedirectToAction(nameof(LifeInsuranceRebuttals));

        if (string.Equals(normalizedQueue, WorkstationLeadBuckets.FinalExpense, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(FinalExpenseRebuttals));

        ViewData["Title"] = $"Workstation — {ResolveQueueTitle(normalizedQueue)}";
        return View("Queue", normalizedQueue);
    }

    // =========================================================
    // Guides
    // =========================================================
    [HttpGet("ProsperAgedMortgageProtection")]
    public IActionResult ProsperAgedMortgageProtection()
    {
        ViewData["Title"] = "Prosper Aged Mortgage Protection";
        return View("ProsperAgedMortgageProtection");
    }

    [HttpGet("FinalExpense")]
    public IActionResult FinalExpense()
    {
        ViewData["Title"] = "Final Expense";
        return View("FinalExpense");
    }

    [HttpGet("LifeInsurance")]
    public IActionResult LifeInsurance()
    {
        ViewData["Title"] = "Life Insurance";
        return View("LifeInsurance");
    }

    // =========================================================
    // Rebuttals
    // =========================================================
    [HttpGet("Rebuttals")]
    public IActionResult Rebuttals()
    {
        ViewData["Title"] = "Mortgage Protection Rebuttals";
        return View("Rebuttals");
    }

    // Backward-compatible alias
    [HttpGet("ProsperAgedMPRebuttals")]
    public IActionResult ProsperAgedMPRebuttals()
    {
        ViewData["Title"] = "Mortgage Protection Rebuttals";
        return View("Rebuttals");
    }

    [HttpGet("FinalExpenseRebuttals")]
    public IActionResult FinalExpenseRebuttals()
    {
        ViewData["Title"] = "Final Expense Rebuttals";
        return View("FinalExpenseRebuttals");
    }

    [HttpGet("LifeInsuranceRebuttals")]
    public IActionResult LifeInsuranceRebuttals()
    {
        ViewData["Title"] = "Life Insurance Rebuttals";
        return View("LifeInsuranceRebuttals");
    }

    private static string ResolveQueueTitle(string queueKey)
        => queueKey switch
        {
            var key when string.Equals(key, WorkstationLeadBuckets.DisabilityInsurance, StringComparison.OrdinalIgnoreCase) => "Disability Insurance Queue",
            var key when string.Equals(key, WorkstationLeadBuckets.AutoInsurance, StringComparison.OrdinalIgnoreCase) => "Auto Insurance Queue",
            var key when string.Equals(key, WorkstationLeadBuckets.HomeInsurance, StringComparison.OrdinalIgnoreCase) => "Home Insurance Queue",
            var key when string.Equals(key, WorkstationLeadBuckets.HealthInsurance, StringComparison.OrdinalIgnoreCase) => "Health Insurance Queue",
            var key when string.Equals(key, WorkstationLeadBuckets.CommercialInsurance, StringComparison.OrdinalIgnoreCase) => "Commercial Insurance Queue",
            _ => "Lead Queue"
        };

    // =========================================================
    // Advanced Markets Planner
    // =========================================================
    [HttpGet("AdvancedMarkets")]
    public IActionResult AdvancedMarkets()
    {
        ViewData["Title"] = "Advanced Markets Planner";
        var vm = new Models.AdvancedMarketsPageViewModel();
        return View("AdvancedMarkets", vm);
    }

    [HttpPost("AdvancedMarkets/Calculate")]
    [IgnoreAntiforgeryToken]
    public IActionResult AdvancedMarketsCalculate(
        [FromServices] AgentPortal.Services.IAdvancedMarketsCalculationService calcService,
        [FromForm] Models.AdvancedMarketsPageViewModel model)
    {
        if (calcService == null)
        {
            Response.StatusCode = 500;
            return Content("Advanced Markets calculate failed: calculation service is not registered.");
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;

            var errors = string.Join(" | ",
                ModelState
                    .Where(kvp => kvp.Value?.Errors?.Count > 0)
                    .SelectMany(kvp => kvp.Value!.Errors.Select(err =>
                        $"{kvp.Key}: {(string.IsNullOrWhiteSpace(err.ErrorMessage) ? err.Exception?.Message : err.ErrorMessage)}"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

            return Content($"Advanced Markets calculate failed: invalid form submission. {errors}");
        }

        try
        {
            model.Result = calcService.Calculate(model);

            return PartialView(
                "~/Views/Workstation/_AdvancedMarketsResultsSummary.cshtml",
                model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AdvancedMarketsCalculate: calculation failed");
            Response.StatusCode = 500;
            return Content("Advanced Markets calculation failed. Please try again or contact support.");
        }
    }
}
