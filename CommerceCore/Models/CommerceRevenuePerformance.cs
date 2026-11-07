namespace ParfaitApp.Models;

/// <summary>Direction of recorded order revenue, not profit or a sales target.</summary>
public sealed record CommerceRevenuePerformance(int CurrentCents, int PreviousCents)
{
    public string Tone => PreviousCents == 0 || CurrentCents == PreviousCents
        ? "warning" : CurrentCents > PreviousCents ? "success" : "danger";
    public string Label => PreviousCents == 0
        ? "No prior revenue baseline"
        : CurrentCents == PreviousCents ? "Revenue unchanged"
        : CurrentCents > PreviousCents ? "Revenue increased" : "Revenue decreased";
    public decimal? ChangePercent => PreviousCents <= 0 ? null
        : Math.Round(((decimal)CurrentCents - PreviousCents) / PreviousCents * 100m, 1);
    public string WindowLabel => "Last 30 days vs previous 30 days · UTC";
}
