using Domain.Billing;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Billing.Square;

public sealed class SquareBillingOptions
{
    public string? ApplicationId { get; init; }
    public string? AccessToken { get; init; }
    public string? LocationId { get; init; }
    public string? WebhookSignatureKey { get; init; }
    public string? WebhookNotificationUrl { get; init; }
    public BillingProviderEnvironment Environment { get; init; } = BillingProviderEnvironment.Sandbox;
    public string SquareVersion { get; init; } = "2026-07-15";
    public int TimeoutSeconds { get; init; } = 30;

    public static SquareBillingOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredEnvironment = configuration["Square:Environment"];
        return new SquareBillingOptions
        {
            ApplicationId = FirstConfigured(configuration,
                "Square:ApplicationId",
                "Square:AppId",
                "Square:PublicApplicationId",
                "Square:WebPaymentsApplicationId"),
            AccessToken = FirstConfigured(configuration,
                "Square:AccessToken",
                "Square:Token",
                "Square:SecretAccessToken"),
            LocationId = FirstConfigured(configuration,
                "Square:LocationId",
                "Square:PublicLocationId",
                "Square:WebPaymentsLocationId"),
            WebhookSignatureKey = FirstConfigured(configuration,
                "Square:WebhookSignatureKey",
                "Square:SignatureKey"),
            WebhookNotificationUrl = FirstConfigured(configuration,
                "Square:WebhookNotificationUrl",
                "Square:WebhookUrl"),
            Environment = ParseEnvironment(configuredEnvironment),
            SquareVersion = FirstConfigured(configuration,
                "Square:SquareVersion",
                "Square:Version")
                ?? "2026-07-15",
            TimeoutSeconds = int.TryParse(configuration["Square:TimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0
                ? timeoutSeconds
                : 30
        };
    }

    public string GetBaseUrl()
    {
        return Environment == BillingProviderEnvironment.Production
            ? "https://connect.squareup.com"
            : "https://connect.squareupsandbox.com";
    }

    public bool HasServerCredentials() =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        !string.IsNullOrWhiteSpace(LocationId);

    public bool HasBrowserCredentials() =>
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(LocationId);

    public bool HasWebhookSignatureConfiguration() =>
        !string.IsNullOrWhiteSpace(WebhookSignatureKey);

    private static BillingProviderEnvironment ParseEnvironment(string? value)
    {
        return string.Equals(value?.Trim(), "Production", StringComparison.OrdinalIgnoreCase)
            ? BillingProviderEnvironment.Production
            : BillingProviderEnvironment.Sandbox;
    }

    private static string? FirstConfigured(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var normalized = Normalize(configuration[key]);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
