namespace Protect_Website.Models;

public sealed class QuoteAgentHandoffViewModel
{
    public string LabelName { get; init; } = string.Empty;
    public string IntroLine { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string Initials { get; init; } = "AG";
    public string Npn { get; init; } = string.Empty;
}
