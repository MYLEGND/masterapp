using LegendApp.Services.Budget.Interfaces;

namespace LegendApp.Services.Budget
{
    public class BudgetCalculator : IBudgetCalculator
    {
        public decimal CalculateNetWorth(decimal assets, decimal liabilities)
        {
            return assets - liabilities;
        }
    }
}
