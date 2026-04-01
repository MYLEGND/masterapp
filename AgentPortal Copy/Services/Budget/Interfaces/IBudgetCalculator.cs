namespace LegendApp.Services.Budget.Interfaces
{
    public interface IBudgetCalculator
    {
        decimal CalculateNetWorth(decimal assets, decimal liabilities);
    }
}
