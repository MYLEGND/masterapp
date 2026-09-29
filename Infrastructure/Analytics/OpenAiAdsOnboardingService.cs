using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IOpenAiAdsOnboardingService
{
    Task<OpenAiAdsOnboardingSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default);
}

public sealed class OpenAiAdsOnboardingService(
    MasterAppDbContext db,
    IPlatformConnectionHealthAuthority runtimeHealth,
    IOpenAiAdsExecutionService ads,
    IBusinessPublicUrlResolver businessUrls,
    IConfiguration configuration) : IOpenAiAdsOnboardingService
{
    public async Task<OpenAiAdsOnboardingSnapshot> GetAsync(MarketingOwnerScope owner, CancellationToken ct = default)
    {
        var runtime = await runtimeHealth.ReadAsync(owner, ct);
        var provider = runtime.OpenAi;
        var connection = provider.Connection;
        var health = provider.Delivery;
        var providerVerified = provider.ProviderVerified;

        var conversionMapped = false;
        if (providerVerified && connection.HasManagementCredential)
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
            Step("connection", "ChatGPT Ads connected", providerVerified,
                providerVerified ? "verified" : connection.Connected ? provider.Status : "not_connected",
                providerVerified
                    ? "The scoped advertiser connection was verified live against the provider."
                    : connection.Connected
                        ? provider.Error ?? "The stored advertiser connection could not be verified live."
                        : "Connect the scoped ChatGPT Ads advertiser account."),
            Step("account", "Advertiser account exists", providerVerified && provider.Account is not null,
                providerVerified && provider.Account is not null ? "verified" : "missing_or_unverified",
                providerVerified && provider.Account is not null
                    ? provider.Account.AccountName ?? provider.Account.AccountId
                    : "No live provider advertiser account has been verified."),
            Step("authorization", "LEGEND authorized", providerVerified && connection.HasManagementCredential,
                providerVerified && connection.HasManagementCredential ? "authorized" : "not_authorized",
                providerVerified && connection.HasManagementCredential
                    ? $"Authorization method: {connection.AuthorizationMethod ?? "provider credential"}."
                    : "A live management-capable advertiser authorization is required."),
            Step("account_selected", "Account selected", providerVerified && !string.IsNullOrWhiteSpace(connection.AccountId),
                providerVerified && !string.IsNullOrWhiteSpace(connection.AccountId) ? "selected" : "not_selected",
                providerVerified && !string.IsNullOrWhiteSpace(connection.AccountId)
                    ? "The live-verified scoped account is the canonical selected account."
                    : "Select and verify the advertiser account."),
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
        var ready = providerVerified &&
                    complete == steps.Count &&
                    connection.AccountApproved &&
                    (health.Status is "configured_no_delivery_evidence" or "provider_accepted");
        var overall = ready ? "ready_to_advertise"
            : providerVerified ? "setup_incomplete"
            : connection.Connected ? "provider_unverified"
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

        var states = await db.Set<WebsiteContentState>().AsNoTracking().Where(x => x.PublishedVersionId != null).ToListAsync(ct);
        foreach (var state in states)
            if (await db.Set<WebsiteContentVersion>().AsNoTracking().AnyAsync(v => v.Id == state.PublishedVersionId && v.StateId == state.Id, ct) &&
                await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, state, ct) == owner)
                return true;
        return false;
    }

    private static OpenAiAdsReadinessStep Step(string key, string label, bool complete, string status, string detail) =>
        new(key, label, complete, status, detail);
}
