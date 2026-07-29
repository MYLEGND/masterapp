namespace Infrastructure.Social.Spotify;

public sealed class SpotifySocialMusicOptions
{
    public const string SectionName = "Spotify";

    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Market { get; set; } = "US";
    public int SearchLimit { get; set; } = 10;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
