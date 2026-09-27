using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiAdsOnboardingService
{
    Task<OpenAiAdsOnboardingSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default);
}

public sealed class OpenAiAdsOnboardingService(
    MasterAppDbContext db,
    IOpenAiAdsAccountConnectionAuthority connections,
    IOpenAiMeasurementHealthService measurementHealth,
    IOpenAiAdsExecutionService ads,
    IBusinessPublicUrlResolver businessUrls) : IOpenAiAdsOnboardingService
{
    public async Task<OpenAiAdsOnboardingSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var connection = await connections.GetAsync(owner, ct);
        var health = await measurementHealth.GetAsync(owner, ct);

        var conversionMapped = false;
        if (connection.Connected && connection.HasManagementCredential)
        {
            try
            {
                var settings = await ads.ListConversionEventSettingsAsync(owner, ct);
                conversionMapped =
                    settings.Payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    settings.Payload.TryGetProperty("data", out var data) &&
                    data.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    data.EnumerateArray().Any(x =>
                        x.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        (!x.TryGetProperty("status", out var status) ||
                         !string.Equals(status.GetString(), "archived", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OpenAiAdsExecutionException)
            {
                conversionMapped = false;
            }
        }

        var websiteVerified = await WebsiteVerifiedAsync(owner, ct);
        var steps = new List<OpenAiAdsReadinessStep>
        {
            Step("connection", "ChatGPT Ads connected", connection.Connected,
                connection.Connected ? "connected" : "not_connected",
                connection.Connected ? "A scoped advertiser connection is stored." : "Connect the scoped ChatGPT Ads advertiser account."),
            Step("account", "Advertiser account exists", !string.IsNullOrWhiteSpace(connection.AccountId),
                !string.IsNullOrWhiteSpace(connection.AccountId) ? "verified" : "missing",
                !string.IsNullOrWhiteSpace(connection.AccountId) ? connection.AccountName ?? connection.AccountId! : "No provider advertiser account has been verified."),
            Step("authorization", "LEGEND authorized", connection.HasManagementCredential,
                connection.HasManagementCredential ? "authorized" : "not_authorized",
                connection.HasManagementCredential ? $"Authorization method: {connection.AuthorizationMethod ?? "provider credential"}." : "A management-capable advertiser credential is required."),
            Step("account_selected", "Account selected", !string.IsNullOrWhiteSpace(connection.AccountId),
                !string.IsNullOrWhiteSpace(connection.AccountId) ? "selected" : "not_selected",
                !string.IsNullOrWhiteSpace(connection.AccountId) ? "The verified scoped account is the canonical selected account." : "Select and verify the advertiser account."),
            Step("pixel", "OpenAI Pixel configured", connection.PixelConfigured,
                connection.PixelConfigured ? "configured" : "not_configured",
                connection.PixelConfigured ? "The canonical provider Pixel is bound to this scope." : "Configure the OpenAI Pixel."),
            Step("capi", "Conversions API configured", connection.ConversionsApiConfigured,
                connection.ConversionsApiConfigured ? "configured" : "not_configured",
                connection.ConversionsApiConfigured ? "The scoped server conversion credential is protected and available." : "Configure the Conversions API credential."),
            Step("conversions", "Conversions mapped", conversionMapped,
                conversionMapped ? "mapped" : "not_mapped",
                conversionMapped ? "At least one active provider conversion setting is available." : "Map at least one canonical conversion destination."),
            Step("website", "Website verified", websiteVerified,
                websiteVerified ? "verified" : "not_verified",
                websiteVerified ? "The scoped public website destination is published and verified." : "Publish and verify the scoped website destination.")
        };

        var complete = steps.Count(x => x.Complete);
        var ready = complete == steps.Count &&
                    connection.AccountApproved &&
                    string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase);
        var overall = ready ? "ready_to_advertise"
            : connection.Connected ? "setup_incomplete"
            : "not_connected";

        return new(owner, overall, ready, complete, steps.Count, connection, health, steps);
    }

    private async Task<bool> WebsiteVerifiedAsync(MarketingOwnerScope owner, CancellationToken ct)
    {
        if (owner.CommerceBusinessId is Guid businessId)
        {
            try
            {
                _ = await businessUrls.ResolveAsync(businessId, ct);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return await db.Set<WebsiteContentState>().AsNoTracking()
            .AnyAsync(x => x.OwnerKey == owner.Key && x.PublishedVersionId != null, ct);
    }

    private static OpenAiAdsReadinessStep Step(string key, string label, bool complete, string status, string detail) =>
        new(key, label, complete, status, detail);
}
