namespace LegendApp.Models.Budget
{
    public class WealthForecastInput
    {
        public double AnnualIncome { get; set; }
        public int WorkingYears { get; set; }
        public double Inflation { get; set; }          // %
        public double AfterTaxReturn { get; set; }     // %
        public double TaxRate { get; set; }            // %
        public double FixedLiabilities { get; set; }   // %
        public double LifestyleSpending { get; set; }  // %
    }
}
