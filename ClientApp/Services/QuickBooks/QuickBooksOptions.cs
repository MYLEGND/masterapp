namespace ClientApp.Services.QuickBooks;

public sealed class QuickBooksOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "com.intuit.quickbooks.accounting";
    public string AuthorizationEndpoint { get; set; } = "https://appcenter.intuit.com/connect/oauth2";
    public string TokenEndpoint { get; set; } = "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer";
    public string ApiBaseUrl { get; set; } = "https://quickbooks.api.intuit.com/v3/company";
    public int SnapshotTtlMinutes { get; set; } = 30;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RedirectUri);
}
