using System.Net.Http.Headers;
using System.Text.Json;
using Infrastructure.Bookings;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed record MetaProviderRuntimeHealth(
    bool Exists,
    bool StoredConnected,
    bool ProviderVerified,
    string Status,
    string? AccountId,
    string? AccountName,
    DateTime CheckedUtc,
    int? HttpStatusCode,
    string? Error);

public sealed record OpenAiProviderRuntimeHealth(
    OpenAiAdsConnectionSnapshot Connection,
    OpenAiAdsProviderAccountSnapshot? Account,
    OpenAiAdsMeasurementCapabilitySnapshot? Capability,
    OpenAiMeasurementHealthSnapshot Delivery,
    bool ProviderVerified,
    string Status,
    DateTime CheckedUtc,
    string? Error);

public sealed record CalendarProviderRuntimeHealth(
    MicrosoftCalendarConnectionSnapshot Connection,
    bool ProviderVerified,
    string Status,
    DateTime CheckedUtc,
    int? HttpStatusCode,
    string? Error);

public sealed record SignalRuntimeHealth(
    MarketingMeasurementEvidenceSnapshot Evidence,
    bool Operational,
    string Status,
    int FailedDeliveries,
    int RetryableDeliveries,
    int PendingDeliveries,
    DateTime CheckedUtc);

public sealed record PlatformConnectionHealthSnapshot(
    MarketingOwnerScope Owner,
    MetaProviderRuntimeHealth Meta,
    OpenAiProviderRuntimeHealth OpenAi,
    CalendarProviderRuntimeHealth Calendar,
    SignalRuntimeHealth Signals);

public interface IPlatformConnectionHealthAuthority
{
    Task<PlatformConnectionHealthSnapshot> ReadAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical live-health authority for owner-scoped external connections and signal delivery.
///
/// Stored credentials/configuration are inventory only. A provider is healthy only after a
/// read-only provider capability check succeeds for the exact stored owner/account. Analytics
/// delivery health comes only from durable canonical source/destination evidence.
///
/// This authority never creates campaigns, changes provider configuration, substitutes credentials,
/// or falls back to another owner.
/// </summary>
public sealed class PlatformConnectionHealthAuthority(
    MarketingConnectionStore metaConnections,
    IOpenAiAdsAccountConnectionAuthority openAiConnections,
    IOpenAiAdsDirectConnectionService openAiDirect,
    IOpenAiMeasurementHealthService openAiDelivery,
    IMicrosoftCalendarConnectionAuthority calendar,
    MarketingMeasurementEvidenceService evidence,
    IHttpClientFactory httpClientFactory,
    ILogger<PlatformConnectionHealthAuthority> logger) : IPlatformConnectionHealthAuthority
{
    public async Task<PlatformConnectionHealthSnapshot> ReadAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var meta = await VerifyMetaAsync(owner, cancellationToken);
        var openAi = await VerifyOpenAiAsync(owner, cancellationToken);
        var calendarHealth = await VerifyCalendarAsync(owner, cancellationToken);
        var signals = await ReadSignalHealthAsync(owner, cancellationToken);

        return new(owner, meta, openAi, calendarHealth, signals);
    }

    private async Task<MetaProviderRuntimeHealth> VerifyMetaAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        var checkedUtc = DateTime.UtcNow;
        var stored = await metaConnections.GetStatusAsync(owner, MarketingDestinationKeys.Meta, cancellationToken);
        if (stored is null)
            return new(false, false, false, "not_configured", null, null, checkedUtc, null, null);

        var storedConnected =
            stored.ConnectedUtc.HasValue &&
            !stored.DisconnectedUtc.HasValue &&
            !string.IsNullOrWhiteSpace(stored.AdsAccessTokenCiphertext) &&
            stored.AccessTokenExpiresUtc > DateTime.UtcNow;

        if (!storedConnected)
            return new(true, false, false, "stored_connection_unusable",
                stored.AdAccountId, stored.AdAccountName, checkedUtc, null,
                "Stored Meta connection is disconnected, expired, or missing its credential.");

        MetaAdsConnectionRecord? connection;
        try
        {
            connection = await metaConnections.GetAdsAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Meta credential verification failed for owner {OwnerKey}.", owner.Key);
            return new(true, true, false, "credential_unreadable",
                stored.AdAccountId, stored.AdAccountName, checkedUtc, null,
                "Stored Meta credential could not be read by the canonical credential authority.");
        }

        if (connection is null || string.IsNullOrWhiteSpace(connection.AccountId))
            return new(true, true, false, "account_binding_missing",
                stored.AdAccountId, stored.AdAccountName, checkedUtc, null,
                "Meta connection has no usable ad-account binding.");

        var accountId = NormalizeMetaAccountId(connection.AccountId);
        var token = connection.AccessToken;
        var client = httpClientFactory.CreateClient("ResilientDefault");

        var fields = "id,name,account_status,disable_reason";
        var accountUrl =
            $"{MetaGraphEndpointAuthority.Graph($"act_{accountId}")}?fields={Uri.EscapeDataString(fields)}&access_token={Uri.EscapeDataString(token)}";
        var accountResponse = await MetaGraphEndpointAuthority.GetAsync(client, accountUrl, cancellationToken);
        if (!accountResponse.IsSuccessStatusCode)
        {
            var error = MetaGraphEndpointAuthority.SafeErrorMessage(
                accountResponse.Body,
                "Meta rejected the stored ad-account connection.");
            return new(true, true, false, "provider_rejected",
                accountId, connection.AccountName, checkedUtc, (int)accountResponse.StatusCode, error);
        }

        string? providerName = connection.AccountName;
        var active = false;
        var disableReason = 0;
        try
        {
            using var doc = JsonDocument.Parse(accountResponse.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                providerName = name.GetString();
            active = root.TryGetProperty("account_status", out var status) &&
                     status.TryGetInt32(out var accountStatus) &&
                     accountStatus == 1;
            if (root.TryGetProperty("disable_reason", out var disabled) &&
                disabled.TryGetInt32(out var parsedDisableReason))
                disableReason = parsedDisableReason;
        }
        catch (JsonException)
        {
            return new(true, true, false, "provider_response_invalid",
                accountId, providerName, checkedUtc, (int)accountResponse.StatusCode,
                "Meta returned an invalid ad-account verification response.");
        }

        if (!active || disableReason != 0)
            return new(true, true, false, "account_inactive",
                accountId, providerName, checkedUtc, (int)accountResponse.StatusCode,
                $"Meta ad account is not active (disable reason {disableReason}).");

        // Account metadata alone does not prove ads_read can read campaign objects.
        // This read is intentionally bounded and non-mutating.
        var campaignUrl =
            $"{MetaGraphEndpointAuthority.Graph($"act_{accountId}/campaigns")}?fields=id&limit=1&access_token={Uri.EscapeDataString(token)}";
        var campaignResponse = await MetaGraphEndpointAuthority.GetAsync(client, campaignUrl, cancellationToken);
        if (!campaignResponse.IsSuccessStatusCode)
        {
            var error = MetaGraphEndpointAuthority.SafeErrorMessage(
                campaignResponse.Body,
                "Meta campaign-read verification failed.");
            return new(true, true, false, "campaign_read_failed",
                accountId, providerName, checkedUtc, (int)campaignResponse.StatusCode, error);
        }

        return new(true, true, true, "verified",
            accountId, providerName, checkedUtc, (int)campaignResponse.StatusCode, null);
    }

    private async Task<OpenAiProviderRuntimeHealth> VerifyOpenAiAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        var checkedUtc = DateTime.UtcNow;
        OpenAiAdsConnectionSnapshot connection;
        try
        {
            connection = await openAiConnections.GetAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI Ads stored connection read failed for owner {OwnerKey}.", owner.Key);
            return new(
                EmptyOpenAiConnection(owner),
                null,
                null,
                EmptyOpenAiDelivery(owner, "connection_unavailable"),
                false,
                "connection_unavailable",
                checkedUtc,
                "Stored OpenAI Ads connection could not be read.");
        }

        var delivery = await ReadOpenAiDeliveryAsync(owner, cancellationToken);
        if (!connection.Connected)
            return new(connection, null, null, delivery, false, "not_configured", checkedUtc, null);

        try
        {
            var account = await openAiDirect.InspectAsync(owner, cancellationToken);
            var capability = await openAiDirect.InspectMeasurementAsync(owner, cancellationToken);
            var matchesStoredAccount =
                account is not null &&
                !string.IsNullOrWhiteSpace(connection.AccountId) &&
                string.Equals(account.AccountId, connection.AccountId, StringComparison.Ordinal);

            if (!matchesStoredAccount)
                return new(connection, account, capability, delivery, false, "account_mismatch", checkedUtc,
                    "OpenAI Ads provider account did not match the stored canonical account binding.");

            return new(connection, account, capability, delivery, true, "verified", checkedUtc, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI Ads live verification failed for owner {OwnerKey}.", owner.Key);
            return new(connection, null, null, delivery, false, "provider_unavailable", checkedUtc,
                "OpenAI Ads provider verification failed for the stored account.");
        }
    }

    private async Task<CalendarProviderRuntimeHealth> VerifyCalendarAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        var checkedUtc = DateTime.UtcNow;
        MicrosoftCalendarConnectionSnapshot connection;
        try
        {
            connection = await calendar.GetAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Microsoft Calendar stored connection read failed for owner {OwnerKey}.", owner.Key);
            return new(
                EmptyCalendarConnection(owner),
                false,
                "connection_unavailable",
                checkedUtc,
                null,
                "Stored Microsoft Calendar connection could not be read.");
        }

        if (!connection.Connected)
            return new(connection, false, "not_configured", checkedUtc, null, null);

        try
        {
            var token = await calendar.GetAccessTokenAsync(owner, cancellationToken);
            var client = httpClientFactory.CreateClient("ResilientDefault");

            var identityUrl = string.Equals(
                    connection.AuthorizationMethod,
                    MicrosoftCalendarConnectionAuthority.ApplicationAuthorization,
                    StringComparison.OrdinalIgnoreCase)
                ? $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(connection.Email ?? string.Empty)}?$select=id,displayName,mail,userPrincipalName"
                : "https://graph.microsoft.com/v1.0/me?$select=id,displayName,mail,userPrincipalName";

            if (identityUrl.Contains("/users/?", StringComparison.Ordinal))
                return new(connection, false, "identity_missing", checkedUtc, null,
                    "Microsoft Calendar stored connection has no provider mailbox identity.");

            using var identityRequest = new HttpRequestMessage(HttpMethod.Get, identityUrl);
            identityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var identityResponse = await client.SendAsync(identityRequest, cancellationToken);
            var identityBody = await identityResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!identityResponse.IsSuccessStatusCode)
                return new(connection, false, "identity_read_failed", checkedUtc, (int)identityResponse.StatusCode,
                    $"Microsoft Graph identity verification failed with HTTP {(int)identityResponse.StatusCode}.");

            if (!IdentityMatches(connection.UserId, identityBody))
                return new(connection, false, "identity_mismatch", checkedUtc, (int)identityResponse.StatusCode,
                    "Microsoft Graph identity no longer matches the stored canonical calendar owner.");

            // Prove the Bookings permission/capability rather than trusting the stored scope list.
            using var bookingsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/solutions/bookingBusinesses?$top=1&$select=id,displayName");
            bookingsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var bookingsResponse = await client.SendAsync(bookingsRequest, cancellationToken);
            if (!bookingsResponse.IsSuccessStatusCode)
                return new(connection, false, "bookings_read_failed", checkedUtc, (int)bookingsResponse.StatusCode,
                    $"Microsoft Bookings verification failed with HTTP {(int)bookingsResponse.StatusCode}.");

            return new(connection, true, "verified", checkedUtc, (int)bookingsResponse.StatusCode, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Microsoft Calendar live verification failed for owner {OwnerKey}.", owner.Key);
            return new(connection, false, "provider_unavailable", checkedUtc, null,
                "Microsoft Calendar provider verification failed for the stored connection.");
        }
    }

    private async Task<SignalRuntimeHealth> ReadSignalHealthAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        var checkedUtc = DateTime.UtcNow;
        try
        {
            var snapshot = await evidence.GetAsync(owner, cancellationToken);
            var failed = snapshot.Meta.Failed + snapshot.OpenAi.Failed;
            var retrying = snapshot.Meta.Retrying + snapshot.OpenAi.Retrying;
            var pending = snapshot.Meta.Pending + snapshot.OpenAi.Pending;
            var operational = failed == 0;
            var status = failed > 0
                ? "delivery_failures"
                : retrying > 0
                    ? "retrying"
                    : pending > 0
                        ? "pending"
                        : snapshot.ReceivingEvents
                            ? "observed"
                            : "quiet";

            return new(snapshot, operational, status, failed, retrying, pending, checkedUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Canonical marketing signal evidence read failed for owner {OwnerKey}.", owner.Key);
            return new(
                EmptyEvidence(owner),
                false,
                "evidence_unavailable",
                0,
                0,
                0,
                checkedUtc);
        }
    }

    private async Task<OpenAiMeasurementHealthSnapshot> ReadOpenAiDeliveryAsync(
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        try
        {
            return await openAiDelivery.GetAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI delivery-health read failed for owner {OwnerKey}.", owner.Key);
            return EmptyOpenAiDelivery(owner, "unavailable");
        }
    }

    private static bool IdentityMatches(string? expectedUserId, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var actual = doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            return string.IsNullOrWhiteSpace(expectedUserId) ||
                   string.Equals(expectedUserId, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeMetaAccountId(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("act_", StringComparison.OrdinalIgnoreCase)
            ? normalized[4..]
            : normalized;
    }

    private static OpenAiAdsConnectionSnapshot EmptyOpenAiConnection(MarketingOwnerScope owner) =>
        new(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [],
            null, null, false, false, null, null, null);

    private static OpenAiMeasurementHealthSnapshot EmptyOpenAiDelivery(
        MarketingOwnerScope owner,
        string status) =>
        new(owner, false, false, false, null, 0, 0, 0, 0, null, false, 0, status);

    private static MicrosoftCalendarConnectionSnapshot EmptyCalendarConnection(MarketingOwnerScope owner) =>
        new(owner, false, false, Guid.Empty, null, null, null, null, [], null, null, null, null);

    private static MarketingMeasurementEvidenceSnapshot EmptyEvidence(MarketingOwnerScope owner) =>
        new(
            owner.Key,
            DateTime.UtcNow.AddDays(-30),
            false,
            null,
            new ProviderMeasurementEvidence(0, 0, 0, 0, 0, false, null, "not_observed"),
            new ProviderMeasurementEvidence(0, 0, 0, 0, 0, false, null, "not_observed"));
}
