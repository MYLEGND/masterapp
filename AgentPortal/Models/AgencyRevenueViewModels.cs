using System;
using System.Collections.Generic;
using AgentPortal.Services;

namespace AgentPortal.Models;

public class AgencyRevenueVm
{
    public ProductionTotals Leads { get; set; } = new();
    public ProductionTotals Clients { get; set; } = new();
    public Dictionary<string, ProductionTotals> ByAgent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MonthlyProducerVm> Monthly { get; set; } = new();
    public Dictionary<string, string> AgentNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int CurrentMonth { get; set; }
    public int Year { get; set; }
}

public class MonthlyProducerVm
{
    public int Month { get; set; }
    public List<ProducerMonthVm> Producers { get; set; } = new();

    public ProductionTotals MonthTotals
    {
        get
        {
            var totals = new ProductionTotals();
            foreach (var p in Producers)
            {
                totals.Submitted += p.Totals.Submitted;
                totals.Issued += p.Totals.Issued;
                totals.Paid += p.Totals.Paid;
                totals.CountSubmitted += p.Totals.CountSubmitted;
                totals.CountIssued += p.Totals.CountIssued;
                totals.CountPaid += p.Totals.CountPaid;
                totals.Personal += p.Totals.Personal;
                totals.CountPersonal += p.Totals.CountPersonal;
            }
            return totals;
        }
    }
}

public class ProducerMonthVm
{
    public string AgentUserId { get; set; } = "";
    public ProductionTotals Totals { get; set; } = new();
    public ProductionTotals LeadsTotals { get; set; } = new();
    public ProductionTotals ClientsTotals { get; set; } = new();
    public List<DailyBreakdownVm> Daily { get; set; } = new();
    public List<WeeklyBreakdownVm> Weekly { get; set; } = new();
}

public class DailyBreakdownVm
{
    public DateOnly Date { get; set; }
    public ProductionTotals Totals { get; set; } = new();
}

public class WeeklyBreakdownVm
{
    /// <summary>Week bucket start date (local, Monday-based).</summary>
    public DateOnly WeekStart { get; set; }
    public ProductionTotals Totals { get; set; } = new();
}
