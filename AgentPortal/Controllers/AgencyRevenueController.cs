using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using AgentPortal.Models;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[Route("agency-command/revenue")]
public class AgencyRevenueController : Controller
{
    private readonly ProductionService _production;
    private readonly MasterAppDbContext _db;

    public AgencyRevenueController(ProductionService production, MasterAppDbContext db)
    {
        _production = production;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var localTz = ResolveLocalTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, localTz);
        var (leads, clients, byAgent) = await _production.GetAgencyTotalsAsync();
        var monthlyData = await _production.GetMonthlyProducerBreakdownAsync(localTz, nowLocal.Year, nowLocal.Month);

        // Ensure months up to current month appear even if empty.
        var monthly = Enumerable.Range(1, nowLocal.Month)
            .Select(m =>
            {
                var existing = monthlyData.FirstOrDefault(x => x.Month == m);
                return existing ?? new MonthlyProducerVm { Month = m };
            })
            .ToList();

        var names = _db.AgentProfiles.AsNoTracking()
            .ToDictionary(a => a.AgentUserId, a => string.IsNullOrWhiteSpace(a.FullName) ? a.AgentUpn : a.FullName, StringComparer.OrdinalIgnoreCase);

        var vm = new AgencyRevenueVm
        {
            Leads = leads,
            Clients = clients,
            ByAgent = byAgent,
            Monthly = monthly,
            AgentNames = names,
            CurrentMonth = nowLocal.Month,
            Year = nowLocal.Year
        };

        // Use shared view under AgencyCommand folder
        return View("~/Views/AgencyCommand/Revenue.cshtml", vm);
    }

    private static TimeZoneInfo ResolveLocalTimeZone()
    {
        try
        {
            // App-local standard: America/Phoenix
            return TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}
