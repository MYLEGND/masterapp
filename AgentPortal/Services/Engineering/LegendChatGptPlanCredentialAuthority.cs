using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AgentPortal.Services.Engineering;

internal sealed record EngineeringModelOption(string Slug, string DisplayName);
internal sealed record EngineeringModelCatalog(
    bool Ready,
    string Code,
    IReadOnlyList<EngineeringModelOption> Models);

internal interface ILegendEngineeringAgentAdapter
{
    Task<object> GetStatusAsync(CancellationToken cancellationToken);
    Task<EngineeringModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken);
    Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken);
}

internal sealed record ChatGptPlanCredentialState(
    bool Ready,
    string Code,
    string? ClientId,
    string? AccessToken,
    IReadOnlyList<string> GrantedScopes,
    DateTime? ExpiresUtc,
    bool PrivateClientApproved,
    string? ProviderBlockerClass = null,
    string? ProviderBlockerCode = null,
    DateTime? ProviderBlockedUtc = null,
    DateTime? ProviderRetryNotBeforeUtc = null,
    string? ProviderRequestId = null,
    int? ProviderHttpStatus = null,
    string? ProviderErrorParam = null,
    string? ProviderCircuitEpisodeId = null,
    string? ProviderRecoveredEpisodeId = null,
    DateTime? ProviderRecoveredUtc = null,
    int ProviderFailureStreak = 0,
    string ReadinessState = "UNVERIFIED",
    string? ReadinessSignature = null,
    IReadOnlyDictionary<string, string>? ReadinessModels = null,
    DateTime? ReadinessCheckedUtc = null,
    string? ReadinessResponseId = null,
    string? ReadinessRequestId = null,
    string? ReadinessCode = null);

internal sealed record ChatGptPlanProviderFailure(
    string BlockerClass,
    string Code,
    DateTime? RetryNotBeforeUtc,
    string? ProviderRequestId,
    int? HttpStatus,
    string? ErrorParam);

internal sealed record ChatGptPlanProviderExecutionLease(
    bool Acquired,
    string Code,
    string? LeaseIdentity,
    DateTime? LeaseUntilUtc);

public sealed record ChatGptPlanClientRegistrationState(
    bool Configured,
    string Code,
    string? ClientId,
    string AuthenticationMethod,
    bool ClientSecretConfigured,
    bool EligibilityConfirmed);

public sealed record ChatGptPlanAuthorizationStart(
    string AuthorizationUrl,
    string State,
    string Code);

public sealed record ChatGptPlanAuthorizationResult(
    bool Ready,
    string Code);

internal interface ILegendChatGptPlanCredentialAuthority
{
    Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken);
    Task<ChatGptPlanClientRegistrationState> GetClientRegistrationAsync(CancellationToken cancellationToken);

    Task<ChatGptPlanClientRegistrationState> SaveClientRegistrationAsync(
        string clientId,
        string authenticationMethod,
        string? clientSecret,
        CancellationToken cancellationToken);

    Task<ChatGptPlanAuthorizationStart> BeginAuthorizationAsync(CancellationToken cancellationToken);

    Task<ChatGptPlanAuthorizationResult> CompleteAuthorizationAsync(
        string code,
        string state,
        string? responseIssuer,
        string? callbackClientId,
        CancellationToken cancellationToken);

    Task AbortAuthorizationAsync(
        string state,
        CancellationToken cancellationToken);

    Task StoreAuthorizationAsync(
        string clientId,
        string accessToken,
        string refreshToken,
        IReadOnlyList<string> grantedScopes,
        DateTime accessTokenExpiresUtc,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task RecordProviderFailureAsync(
        ChatGptPlanProviderFailure failure,
        CancellationToken cancellationToken);

    Task RecordReadinessSuccessAsync(
        string signature,
        IReadOnlyDictionary<string, string> resolvedModels,
        string? responseId,
        string? providerRequestId,
        CancellationToken cancellationToken);

    Task MarkReadinessUnverifiedAsync(
        string code,
        CancellationToken cancellationToken);

    Task<ChatGptPlanProviderExecutionLease> TryAcquireProviderExecutionLeaseAsync(
        string owner,
        TimeSpan duration,
        bool allowCircuitProbe,
        CancellationToken cancellationToken);

    Task<bool> RenewProviderExecutionLeaseAsync(
        string leaseIdentity,
        TimeSpan duration,
        CancellationToken cancellationToken);

    Task ReleaseProviderExecutionLeaseAsync(
        string leaseIdentity,
        CancellationToken cancellationToken);
}

internal sealed class LegendChatGptPlanCredentialAuthority(
    MasterAppDbContext db,
    IConfiguration configuration,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClientFactory)
    : ILegendChatGptPlanCredentialAuthority
{
    private const string CredentialKey = "founder-chatgpt-plan";
    private const string RegistrationKey = "founder-chatgpt-plan-client";
    private const string Connected = "CONNECTED";
    private const string ReauthorizationRequired = "REAUTHORIZATION_REQUIRED";
    private const string AuthenticationNone = "none";
    private const string AuthenticationBasic = "client_secret_basic";
    private const string ProductionIssuer = "https://auth.openai.com";
    private const string ProductionRedirectUri =
        "https://portal.mylegnd.com/founder/engineering/chatgpt/callback";
    private const string PlanResource = "https://api.openai.com/v1";
    private const string DiscoveryUrl =
        "https://auth.openai.com/.well-known/openid-configuration";
    private const string RequiredScopeString =
        "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _tokenProtector =
        dataProtection.CreateProtector("LEGEND.Engineering.ChatGptPlanCredential.v1");
    private readonly IDataProtector _clientProtector =
        dataProtection.CreateProtector("LEGEND.Engineering.ChatGptPlanClient.v1");
    private readonly IDataProtector _transactionProtector =
        dataProtection.CreateProtector("LEGEND.Engineering.ChatGptPlanOAuthTransaction.v1");

    public async Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registration = await GetClientRegistrationAsync(cancellationToken);
        if (!registration.Configured || string.IsNullOrWhiteSpace(registration.ClientId))
            return Blocked("chatgpt_plan_client_registration_missing", false, null);

        var record = await ReadCredentialAsync(cancellationToken);
        if (record is null)
        {
            if (!await TryImportConfiguredAuthorizationAsync(registration.ClientId, cancellationToken))
            {
                return new(
                    false,
                    registration.EligibilityConfirmed
                        ? "chatgpt_plan_sign_in_required"
                        : "chatgpt_plan_private_client_eligibility_unverified",
                    registration.ClientId,
                    null,
                    [],
                    null,
                    registration.EligibilityConfirmed);
            }
            record = await ReadCredentialAsync(cancellationToken);
        }

        if (record is null)
            return new(
                false,
                registration.EligibilityConfirmed
                    ? "chatgpt_plan_sign_in_required"
                    : "chatgpt_plan_private_client_eligibility_unverified",
                registration.ClientId,
                null,
                [],
                null,
                registration.EligibilityConfirmed);

        if (!string.Equals(record.ClientId, registration.ClientId, StringComparison.Ordinal))
            return new(false, "chatgpt_plan_client_registration_changed", registration.ClientId, null,
                record.Scopes, record.ExpiresUtc, registration.EligibilityConfirmed);
        if (!HasRequiredScopes(record.Scopes))
            return new(false, "chatgpt_plan_usage_scope_missing", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, registration.EligibilityConfirmed);
        if (!string.Equals(record.State, Connected, StringComparison.Ordinal))
            return new(false, "chatgpt_plan_reauthorization_required", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, registration.EligibilityConfirmed);

        if (record.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return Ready(record, registration.EligibilityConfirmed);

        return await RefreshAsync(record, registration, cancellationToken);
    }

    public async Task<ChatGptPlanClientRegistrationState> GetClientRegistrationAsync(
        CancellationToken cancellationToken)
    {
        var record = await ReadClientRegistrationAsync(cancellationToken);
        if (record is null && await TryImportLegacyClientRegistrationAsync(cancellationToken))
            record = await ReadClientRegistrationAsync(cancellationToken);
        return record is null
            ? new(false, "chatgpt_plan_client_registration_missing", null, AuthenticationNone, false, false)
            : RegistrationState(record);
    }

    public async Task<ChatGptPlanClientRegistrationState> SaveClientRegistrationAsync(
        string clientId,
        string authenticationMethod,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        var normalizedMethod = NormalizeAuthenticationMethod(authenticationMethod);
        var suppliedSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim();
        if (suppliedSecret is { Length: > 4096 })
            throw new InvalidOperationException("chatgpt_plan_client_secret_invalid");

        var saved = await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadClientRegistrationAsync(connection, transaction, cancellationToken);
            string? protectedSecret = null;
            var secretChanged = false;

            if (normalizedMethod == AuthenticationBasic)
            {
                if (suppliedSecret is not null)
                {
                    protectedSecret = _clientProtector.Protect(suppliedSecret);
                    secretChanged = current is null ||
                                    !SecretMatches(current.ClientSecretCiphertext, suppliedSecret);
                }
                else if (current is not null &&
                         string.Equals(current.ClientId, normalizedClientId, StringComparison.Ordinal) &&
                         string.Equals(current.AuthenticationMethod, normalizedMethod, StringComparison.Ordinal) &&
                         !string.IsNullOrWhiteSpace(current.ClientSecretCiphertext))
                {
                    protectedSecret = current.ClientSecretCiphertext;
                }
                else
                {
                    throw new InvalidOperationException("chatgpt_plan_client_secret_required");
                }
            }

            var identityChanged = current is null ||
                                  !string.Equals(current.ClientId, normalizedClientId, StringComparison.Ordinal) ||
                                  !string.Equals(current.AuthenticationMethod, normalizedMethod, StringComparison.Ordinal);
            var eligibilityConfirmed = current is not null &&
                                       !identityChanged &&
                                       !secretChanged &&
                                       current.EligibilityConfirmed;

            var next = new ClientRegistrationRecord(
                normalizedClientId,
                normalizedMethod,
                protectedSecret,
                eligibilityConfirmed,
                Guid.NewGuid().ToString("N"),
                DateTime.UtcNow);

            if (current is not null &&
                !identityChanged &&
                !secretChanged &&
                string.Equals(current.ClientSecretCiphertext, protectedSecret, StringComparison.Ordinal))
                return current;

            await UpsertClientRegistrationAsync(connection, transaction, next, cancellationToken);
            await DeleteCredentialAsync(connection, transaction, cancellationToken);
            await DeleteOAuthTransactionsAsync(connection, transaction, cancellationToken);
            return next;
        }, cancellationToken);

        return RegistrationState(saved);
    }

    public async Task<ChatGptPlanAuthorizationStart> BeginAuthorizationAsync(
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(cancellationToken)
            ?? throw new InvalidOperationException("chatgpt_plan_client_registration_missing");
        var discovery = await GetDiscoveryAsync(cancellationToken);
        var redirectUri = ResolveRedirectUri();

        var state = RandomBase64Url(32);
        var verifier = RandomBase64Url(64);
        var nonce = RandomBase64Url(32);
        var challenge = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var stateHash = Hash(state);
        var now = DateTime.UtcNow;

        var transactionRecord = new OAuthTransactionRecord(
            stateHash,
            registration.ClientId,
            _transactionProtector.Protect(verifier),
            _transactionProtector.Protect(nonce),
            redirectUri,
            now,
            now.AddMinutes(10));

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            await DeleteExpiredOAuthTransactionsAsync(connection, transaction, now, cancellationToken);
            await InsertOAuthTransactionAsync(connection, transaction, transactionRecord, cancellationToken);
            return true;
        }, cancellationToken);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = registration.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = RequiredScopeString,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["nonce"] = nonce,
            ["resource"] = PlanResource
        };
        var authorizationUrl = QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, query);
        return new(authorizationUrl, state, "chatgpt_plan_authorization_started");
    }

    public async Task<ChatGptPlanAuthorizationResult> CompleteAuthorizationAsync(
        string code,
        string state,
        string? responseIssuer,
        string? callbackClientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 8192 ||
            string.IsNullOrWhiteSpace(state) || state.Length > 512)
            throw new InvalidOperationException("chatgpt_plan_authorization_callback_invalid");

        var transactionRecord = await ConsumeOAuthTransactionAsync(state, cancellationToken)
            ?? throw new InvalidOperationException("chatgpt_plan_authorization_state_invalid");
        if (transactionRecord.ExpiresUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("chatgpt_plan_authorization_state_expired");

        var registration = await ReadClientRegistrationAsync(cancellationToken)
            ?? throw new InvalidOperationException("chatgpt_plan_client_registration_missing");
        if (!string.Equals(registration.ClientId, transactionRecord.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("chatgpt_plan_client_registration_changed");
        if (!string.IsNullOrWhiteSpace(callbackClientId) &&
            !string.Equals(callbackClientId.Trim(), registration.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("chatgpt_plan_authorization_client_mismatch");

        var discovery = await GetDiscoveryAsync(cancellationToken);
        if (discovery.AuthorizationResponseIssuerSupported)
        {
            if (string.IsNullOrWhiteSpace(responseIssuer) ||
                !string.Equals(responseIssuer, discovery.Issuer, StringComparison.Ordinal))
                throw new InvalidOperationException("chatgpt_plan_authorization_issuer_mismatch");
        }

        string verifier;
        string nonce;
        try
        {
            verifier = _transactionProtector.Unprotect(transactionRecord.CodeVerifierCiphertext);
            nonce = _transactionProtector.Unprotect(transactionRecord.NonceCiphertext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("chatgpt_plan_authorization_state_invalid");
        }

        var token = await ExchangeAuthorizationCodeAsync(
            discovery,
            registration,
            code.Trim(),
            verifier,
            transactionRecord.RedirectUri,
            cancellationToken);

        if (!ValidToken(token.AccessToken) ||
            !ValidToken(token.RefreshToken) ||
            token.ExpiresIn < 60 ||
            !HasRequiredScopes(token.Scopes) ||
            string.IsNullOrWhiteSpace(token.IdToken))
            throw new InvalidOperationException("chatgpt_plan_authorization_invalid");

        await ValidateIdTokenAsync(
            discovery,
            token.IdToken,
            registration.ClientId,
            nonce,
            cancellationToken);

        await StoreAuthorizationAsync(
            registration.ClientId,
            token.AccessToken,
            token.RefreshToken,
            token.Scopes,
            DateTime.UtcNow.AddSeconds(token.ExpiresIn),
            cancellationToken);

        return new(true, "chatgpt_plan_ready");
    }

    public async Task AbortAuthorizationAsync(
        string state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 512)
            return;
        await ConsumeOAuthTransactionAsync(state, cancellationToken);
    }

    public async Task StoreAuthorizationAsync(
        string clientId,
        string accessToken,
        string refreshToken,
        IReadOnlyList<string> grantedScopes,
        DateTime accessTokenExpiresUtc,
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(cancellationToken);
        if (registration is null && await TryImportLegacyClientRegistrationAsync(cancellationToken))
            registration = await ReadClientRegistrationAsync(cancellationToken);
        if (registration is null ||
            !string.Equals(clientId?.Trim(), registration.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("chatgpt_plan_client_registration_mismatch");
        if (!ValidToken(accessToken) || !ValidToken(refreshToken) ||
            accessTokenExpiresUtc <= DateTime.UtcNow.AddMinutes(1))
            throw new InvalidOperationException("chatgpt_plan_authorization_invalid");

        var scopes = NormalizeScopes(grantedScopes);
        if (!HasRequiredScopes(scopes))
            throw new InvalidOperationException("chatgpt_plan_usage_scope_missing");

        var now = DateTime.UtcNow;
        var protectedRecord = new CredentialRecord(
            registration.ClientId,
            _tokenProtector.Protect(accessToken.Trim()),
            _tokenProtector.Protect(refreshToken.Trim()),
            scopes,
            accessTokenExpiresUtc.ToUniversalTime(),
            Connected,
            Guid.NewGuid().ToString("N"),
            null,
            null,
            now,
            null,
            now);

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var currentRegistration = await ReadClientRegistrationAsync(connection, transaction, cancellationToken);
            if (currentRegistration is null ||
                !string.Equals(currentRegistration.ClientId, registration.ClientId, StringComparison.Ordinal))
                throw new InvalidOperationException("chatgpt_plan_client_registration_changed");

            await UpsertCredentialAsync(connection, transaction, protectedRecord, cancellationToken);
            if (!currentRegistration.EligibilityConfirmed)
            {
                await UpsertClientRegistrationAsync(
                    connection,
                    transaction,
                    currentRegistration with
                    {
                        EligibilityConfirmed = true,
                        Revision = Guid.NewGuid().ToString("N"),
                        UpdatedUtc = now
                    },
                    cancellationToken);
            }
            return true;
        }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var credential = await ReadCredentialAsync(cancellationToken);
        var registration = await ReadClientRegistrationAsync(cancellationToken);

        if (credential is not null && registration is not null)
        {
            try
            {
                var refreshToken = _tokenProtector.Unprotect(credential.RefreshTokenCiphertext);
                var discovery = await GetDiscoveryAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(discovery.RevocationEndpoint))
                {
                    var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        discovery.RevocationEndpoint)
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["token"] = refreshToken,
                            ["token_type_hint"] = "refresh_token",
                            ["client_id"] = registration.ClientId
                        })
                    };
                    ApplyClientAuthentication(request, registration);
                    using var _ = await client.SendAsync(request, cancellationToken);
                }
            }
            catch
            {
                // Local disconnect remains authoritative. A failed remote revocation
                // never leaves LEGEND continuing to use the credential.
            }
        }

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            await DeleteCredentialAsync(connection, transaction, cancellationToken);
            await DeleteOAuthTransactionsAsync(connection, transaction, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task RecordProviderFailureAsync(
        ChatGptPlanProviderFailure failure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > 128 ||
            string.IsNullOrWhiteSpace(failure.BlockerClass) || failure.BlockerClass.Length > 48)
            throw new InvalidOperationException("chatgpt_plan_provider_failure_invalid");

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken)
                ?? throw new InvalidOperationException("chatgpt_plan_sign_in_required");
            var now = DateTime.UtcNow;
            var episode = current.ProviderCircuitEpisodeId ?? Guid.NewGuid().ToString("N");
            var next = current with
            {
                ProviderBlockerClass = SafeControlValue(failure.BlockerClass, 48),
                ProviderBlockerCode = SafeControlValue(failure.Code, 128),
                ProviderBlockedUtc = current.ProviderBlockedUtc ?? now,
                ProviderRetryNotBeforeUtc = failure.RetryNotBeforeUtc?.ToUniversalTime(),
                ProviderRequestId = SafeControlValue(failure.ProviderRequestId, 160),
                ProviderHttpStatus = failure.HttpStatus is >= 100 and <= 599 ? failure.HttpStatus : null,
                ProviderErrorParam = SafeControlValue(failure.ErrorParam, 160),
                ProviderCircuitEpisodeId = episode,
                ProviderFailureStreak = Math.Min(current.ProviderFailureStreak + 1, 1000),
                ReadinessState = "BLOCKED",
                ReadinessCode = SafeControlValue(failure.Code, 128),
                ReadinessCheckedUtc = now,
                Revision = Guid.NewGuid().ToString("N"),
                UpdatedUtc = now
            };
            await UpsertCredentialAsync(connection, transaction, next, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task RecordReadinessSuccessAsync(
        string signature,
        IReadOnlyDictionary<string, string> resolvedModels,
        string? responseId,
        string? providerRequestId,
        CancellationToken cancellationToken)
    {
        if (!ValidDigest(signature) || resolvedModels.Count is < 1 or > 8 ||
            resolvedModels.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                                       !ValidModelBindingValue(pair.Value)))
            throw new InvalidOperationException("chatgpt_plan_readiness_evidence_invalid");

        var modelsJson = JsonSerializer.Serialize(
            resolvedModels.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            JsonOptions);
        if (modelsJson.Length > 2000)
            throw new InvalidOperationException("chatgpt_plan_readiness_evidence_invalid");

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken)
                ?? throw new InvalidOperationException("chatgpt_plan_sign_in_required");
            var now = DateTime.UtcNow;
            var recoveredEpisode = current.ProviderCircuitEpisodeId;
            var next = current with
            {
                ProviderBlockerClass = null,
                ProviderBlockerCode = null,
                ProviderBlockedUtc = null,
                ProviderRetryNotBeforeUtc = null,
                ProviderRequestId = SafeControlValue(providerRequestId, 160),
                ProviderHttpStatus = null,
                ProviderErrorParam = null,
                ProviderCircuitEpisodeId = null,
                ProviderRecoveredEpisodeId = recoveredEpisode ?? current.ProviderRecoveredEpisodeId,
                ProviderRecoveredUtc = recoveredEpisode is null ? current.ProviderRecoveredUtc : now,
                ProviderFailureStreak = 0,
                ReadinessState = "READY",
                ReadinessSignature = signature.ToLowerInvariant(),
                ReadinessModelsJson = modelsJson,
                ReadinessCheckedUtc = now,
                ReadinessResponseId = SafeControlValue(responseId, 160),
                ReadinessRequestId = SafeControlValue(providerRequestId, 160),
                ReadinessCode = "chatgpt_plan_inference_ready",
                Revision = Guid.NewGuid().ToString("N"),
                UpdatedUtc = now
            };
            await UpsertCredentialAsync(connection, transaction, next, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task MarkReadinessUnverifiedAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var safeCode = SafeControlValue(code, 128) ?? "chatgpt_plan_readiness_canary_required";
        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            if (current is null) return false;
            var next = current with
            {
                ReadinessState = "UNVERIFIED",
                ReadinessSignature = null,
                ReadinessModelsJson = null,
                ReadinessCheckedUtc = null,
                ReadinessResponseId = null,
                ReadinessRequestId = null,
                ReadinessCode = safeCode,
                Revision = Guid.NewGuid().ToString("N"),
                UpdatedUtc = DateTime.UtcNow
            };
            await UpsertCredentialAsync(connection, transaction, next, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<ChatGptPlanProviderExecutionLease> TryAcquireProviderExecutionLeaseAsync(
        string owner,
        TimeSpan duration,
        bool allowCircuitProbe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 ||
            duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            return new(false, "chatgpt_plan_provider_execution_lease_invalid", null, null);

        return await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            if (current is null)
                return new ChatGptPlanProviderExecutionLease(false, "chatgpt_plan_sign_in_required", null, null);

            var now = DateTime.UtcNow;
            if (!allowCircuitProbe && !string.IsNullOrWhiteSpace(current.ProviderBlockerCode))
                return new(false, current.ProviderBlockerCode, null, current.ProviderRetryNotBeforeUtc);

            if (current.ProviderExecutionLeaseUntilUtc > now &&
                !string.IsNullOrWhiteSpace(current.ProviderExecutionLeaseIdentity))
                return new(false, "chatgpt_plan_provider_execution_busy", null,
                    current.ProviderExecutionLeaseUntilUtc);

            var identity = Guid.NewGuid().ToString("N");
            var until = now.Add(duration);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [ProviderExecutionLeaseIdentity]=@identity,
                    [ProviderExecutionLeaseOwner]=@owner,
                    [ProviderExecutionLeaseUntilUtc]=@until,
                    [UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key
                """;
            Add(command, "@identity", identity);
            Add(command, "@owner", owner.Trim());
            Add(command, "@until", until);
            Add(command, "@updated", now);
            Add(command, "@key", CredentialKey);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? new ChatGptPlanProviderExecutionLease(true, "acquired", identity, until)
                : new ChatGptPlanProviderExecutionLease(false, "chatgpt_plan_provider_execution_unavailable", null, null);
        }, cancellationToken);
    }

    public async Task<bool> RenewProviderExecutionLeaseAsync(
        string leaseIdentity,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseIdentity) || duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromMinutes(5))
            return false;
        return await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            var now = DateTime.UtcNow;
            if (current is null || current.ProviderExecutionLeaseUntilUtc <= now ||
                !string.Equals(current.ProviderExecutionLeaseIdentity, leaseIdentity, StringComparison.Ordinal))
                return false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [ProviderExecutionLeaseUntilUtc]=@until,[UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key AND [ProviderExecutionLeaseIdentity]=@identity
                """;
            Add(command, "@until", now.Add(duration));
            Add(command, "@updated", now);
            Add(command, "@key", CredentialKey);
            Add(command, "@identity", leaseIdentity);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    public async Task ReleaseProviderExecutionLeaseAsync(
        string leaseIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseIdentity)) return;
        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [ProviderExecutionLeaseIdentity]=NULL,
                    [ProviderExecutionLeaseOwner]=NULL,
                    [ProviderExecutionLeaseUntilUtc]=NULL,
                    [UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key AND [ProviderExecutionLeaseIdentity]=@identity
                """;
            Add(command, "@updated", DateTime.UtcNow);
            Add(command, "@key", CredentialKey);
            Add(command, "@identity", leaseIdentity);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<ChatGptPlanCredentialState> RefreshAsync(
        CredentialRecord snapshot,
        ChatGptPlanClientRegistrationState registrationState,
        CancellationToken cancellationToken)
    {
        var leaseIdentity = Guid.NewGuid().ToString("N");
        var claim = await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            if (current is null)
                return new RefreshClaim(false, "chatgpt_plan_sign_in_required", null);
            if (current.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
                return new RefreshClaim(false, "chatgpt_plan_refresh_not_needed", current);
            if (!string.Equals(current.State, Connected, StringComparison.Ordinal))
                return new RefreshClaim(false, "chatgpt_plan_reauthorization_required", current);
            if (current.RefreshLeaseUntilUtc > DateTime.UtcNow &&
                !string.IsNullOrWhiteSpace(current.RefreshLeaseIdentity))
                return new RefreshClaim(false, "chatgpt_plan_refresh_in_progress", current);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [RefreshLeaseIdentity]=@lease,[RefreshLeaseUntilUtc]=@until,[UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key AND [Revision]=@revision
                """;
            Add(command, "@lease", leaseIdentity);
            Add(command, "@until", DateTime.UtcNow.AddMinutes(2));
            Add(command, "@updated", DateTime.UtcNow);
            Add(command, "@key", CredentialKey);
            Add(command, "@revision", current.Revision);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? new RefreshClaim(true, "chatgpt_plan_refresh_claimed", current)
                : new RefreshClaim(false, "chatgpt_plan_refresh_raced", current);
        }, cancellationToken);

        if (!claim.Claimed)
        {
            if (claim.Code == "chatgpt_plan_refresh_not_needed" && claim.Record is not null)
                return Ready(claim.Record, registrationState.EligibilityConfirmed);
            return new(false, claim.Code, snapshot.ClientId, null, snapshot.Scopes,
                snapshot.ExpiresUtc, registrationState.EligibilityConfirmed);
        }

        string refreshToken;
        try
        {
            refreshToken = _tokenProtector.Unprotect(claim.Record!.RefreshTokenCiphertext);
        }
        catch (CryptographicException)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, registrationState.EligibilityConfirmed);
        }

        TokenResponse refreshed;
        try
        {
            refreshed = await RequestRefreshAsync(snapshot.ClientId, refreshToken, cancellationToken);
        }
        catch (ChatGptPlanRefreshException exception) when (!exception.Terminal)
        {
            await ClearRefreshLeaseAsync(leaseIdentity, cancellationToken);
            return new(false, exception.Code, snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, registrationState.EligibilityConfirmed);
        }
        catch (ChatGptPlanRefreshException)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, registrationState.EligibilityConfirmed);
        }

        if (!ValidToken(refreshed.AccessToken) || !ValidToken(refreshed.RefreshToken) ||
            refreshed.ExpiresIn < 60)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, registrationState.EligibilityConfirmed);
        }

        var scopes = refreshed.Scopes.Count == 0
            ? snapshot.Scopes
            : NormalizeScopes(refreshed.Scopes);
        if (!HasRequiredScopes(scopes))
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_usage_scope_missing", snapshot.ClientId, null,
                scopes, null, registrationState.EligibilityConfirmed);
        }

        var now = DateTime.UtcNow;
        var stored = claim.Record! with
        {
            AccessTokenCiphertext = _tokenProtector.Protect(refreshed.AccessToken.Trim()),
            RefreshTokenCiphertext = _tokenProtector.Protect(refreshed.RefreshToken.Trim()),
            Scopes = scopes,
            ExpiresUtc = now.AddSeconds(refreshed.ExpiresIn),
            State = Connected,
            Revision = Guid.NewGuid().ToString("N"),
            RefreshLeaseIdentity = null,
            RefreshLeaseUntilUtc = null,
            LastRefreshedUtc = now,
            UpdatedUtc = now
        };

        var saved = await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            if (current is null ||
                !string.Equals(current.RefreshLeaseIdentity, leaseIdentity, StringComparison.Ordinal))
                return false;
            await UpsertCredentialAsync(connection, transaction, stored, cancellationToken);
            return true;
        }, cancellationToken);

        return saved
            ? Ready(stored, true)
            : new(false, "chatgpt_plan_refresh_outcome_uncertain", stored.ClientId, null,
                stored.Scopes, stored.ExpiresUtc, registrationState.EligibilityConfirmed);
    }

    private async Task<TokenResponse> RequestRefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var registration = await ReadClientRegistrationAsync(cancellationToken)
            ?? throw new ChatGptPlanRefreshException(
                "chatgpt_plan_client_registration_missing", terminal: true);
        if (!string.Equals(registration.ClientId, clientId, StringComparison.Ordinal))
            throw new ChatGptPlanRefreshException(
                "chatgpt_plan_client_registration_changed", terminal: true);

        var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://auth.openai.com/api/accounts/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["resource"] = PlanResource
            })
        };
        ApplyClientAuthentication(request, registration);

        using var response = await client.SendAsync(request, cancellationToken);
        string body;
        try
        {
            body = await ReadBoundedBodyAsync(
                response,
                64 * 1024,
                "chatgpt_plan_refresh_response_too_large",
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new ChatGptPlanRefreshException(exception.Message, terminal: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = ReadError(body);
            var transient = (int)response.StatusCode is 408 or 429 or >= 500 ||
                            error is "temporarily_unavailable" or "server_error";
            throw new ChatGptPlanRefreshException(
                transient
                    ? "chatgpt_plan_refresh_temporarily_unavailable"
                    : "chatgpt_plan_reauthorization_required",
                terminal: !transient);
        }

        try
        {
            return ParseTokenResponse(body);
        }
        catch (InvalidOperationException exception)
        {
            throw new ChatGptPlanRefreshException(exception.Message, terminal: true);
        }
    }

    private async Task<TokenResponse> ExchangeAuthorizationCodeAsync(
        OpenAiDiscovery discovery,
        ClientRegistrationRecord registration,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = registration.ClientId,
                ["code_verifier"] = verifier,
                ["resource"] = PlanResource
            })
        };
        ApplyClientAuthentication(request, registration);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await ReadBoundedBodyAsync(
            response,
            128 * 1024,
            "chatgpt_plan_authorization_response_too_large",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("chatgpt_plan_authorization_exchange_failed");
        return ParseTokenResponse(body);
    }

    private void ApplyClientAuthentication(
        HttpRequestMessage request,
        ClientRegistrationRecord registration)
    {
        if (registration.AuthenticationMethod == AuthenticationNone)
            return;
        if (registration.AuthenticationMethod != AuthenticationBasic ||
            string.IsNullOrWhiteSpace(registration.ClientSecretCiphertext))
            throw new InvalidOperationException("chatgpt_plan_client_secret_required");

        string secret;
        try
        {
            secret = _clientProtector.Unprotect(registration.ClientSecretCiphertext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("chatgpt_plan_client_secret_unavailable");
        }

        var encodedClient = WebUtility.UrlEncode(registration.ClientId);
        var encodedSecret = WebUtility.UrlEncode(secret);
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(encodedClient + ":" + encodedSecret));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    private async Task ValidateIdTokenAsync(
        OpenAiDiscovery discovery,
        string idToken,
        string clientId,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
        using var response = await client.GetAsync(discovery.JwksUri, cancellationToken);
        var jwksBody = await ReadBoundedBodyAsync(
            response,
            256 * 1024,
            "chatgpt_plan_jwks_response_too_large",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("chatgpt_plan_id_token_validation_failed");

        IEnumerable<SecurityKey> keys;
        try
        {
            keys = new JsonWebKeySet(jwksBody).GetSigningKeys();
        }
        catch
        {
            throw new InvalidOperationException("chatgpt_plan_id_token_validation_failed");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidateIssuer = true,
            ValidIssuer = discovery.Issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, parameters, out _);
            var nonce = principal.FindFirst("nonce")?.Value;
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
                throw new SecurityTokenValidationException("nonce mismatch");
        }
        catch
        {
            throw new InvalidOperationException("chatgpt_plan_id_token_validation_failed");
        }
    }

    private async Task<OpenAiDiscovery> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
        using var response = await client.GetAsync(DiscoveryUrl, cancellationToken);
        var body = await ReadBoundedBodyAsync(
            response,
            128 * 1024,
            "chatgpt_plan_discovery_response_too_large",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("chatgpt_plan_discovery_unavailable");

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var issuer = ReadString(root, "issuer");
            var authorization = ReadString(root, "authorization_endpoint");
            var token = ReadString(root, "token_endpoint");
            var jwks = ReadString(root, "jwks_uri");
            var revocation = ReadString(root, "revocation_endpoint");
            var issuerSupported =
                root.TryGetProperty("authorization_response_iss_parameter_supported", out var supported) &&
                supported.ValueKind == JsonValueKind.True;

            if (!string.Equals(issuer, ProductionIssuer, StringComparison.Ordinal) ||
                !TrustedAuthEndpoint(authorization) ||
                !TrustedAuthEndpoint(token) ||
                !TrustedAuthEndpoint(jwks) ||
                (!string.IsNullOrWhiteSpace(revocation) && !TrustedAuthEndpoint(revocation)))
                throw new InvalidOperationException("chatgpt_plan_discovery_invalid");

            return new(
                issuer!,
                authorization!,
                token!,
                jwks!,
                revocation,
                issuerSupported);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("chatgpt_plan_discovery_invalid");
        }
    }

    private static bool TrustedAuthEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase);

    private string ResolveRedirectUri()
    {
        var configured = configuration["LegendEngineering:ChatGptPlan:RedirectUri"]?.Trim();
        var value = string.IsNullOrWhiteSpace(configured) ? ProductionRedirectUri : configured;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "https" ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.Equals(uri.AbsolutePath, "/founder/engineering/chatgpt/callback", StringComparison.Ordinal))
            throw new InvalidOperationException("chatgpt_plan_redirect_uri_invalid");
        return uri.GetLeftPart(UriPartial.Path);
    }

    private async Task<bool> TryImportLegacyClientRegistrationAsync(
        CancellationToken cancellationToken)
    {
        var clientId = configuration["LegendEngineering:ChatGptPlan:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            return false;

        var normalizedClientId = NormalizeClientId(clientId);
        var secret = configuration["LegendEngineering:ChatGptPlan:ClientSecret"]?.Trim();
        var configuredMethod =
            configuration["LegendEngineering:ChatGptPlan:TokenEndpointAuthenticationMethod"]?.Trim();
        var method = NormalizeAuthenticationMethod(
            string.IsNullOrWhiteSpace(configuredMethod)
                ? string.IsNullOrWhiteSpace(secret) ? AuthenticationNone : AuthenticationBasic
                : configuredMethod);

        if (method == AuthenticationBasic && string.IsNullOrWhiteSpace(secret))
            return false;

        var record = new ClientRegistrationRecord(
            normalizedClientId,
            method,
            method == AuthenticationBasic ? _clientProtector.Protect(secret!) : null,
            configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:PrivateClientApproved") == true,
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow);

        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadClientRegistrationAsync(connection, transaction, cancellationToken);
            if (current is not null) return false;
            await UpsertClientRegistrationAsync(connection, transaction, record, cancellationToken);
            return true;
        }, cancellationToken);
        return true;
    }

    private async Task<bool> TryImportConfiguredAuthorizationAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        var access = configuration["LegendEngineering:ChatGptPlan:AccessToken"]?.Trim();
        var refresh = configuration["LegendEngineering:ChatGptPlan:RefreshToken"]?.Trim();
        var scopes = (configuration["LegendEngineering:ChatGptPlan:GrantedScopes"] ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!DateTime.TryParse(
                configuration["LegendEngineering:ChatGptPlan:AccessTokenExpiresUtc"],
                null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var expires) ||
            !ValidToken(access) || !ValidToken(refresh) || !HasRequiredScopes(scopes))
            return false;

        await StoreAuthorizationAsync(clientId, access!, refresh!, scopes, expires, cancellationToken);
        return true;
    }

    private bool SecretMatches(string? ciphertext, string supplied)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return false;
        try
        {
            return string.Equals(
                _clientProtector.Unprotect(ciphertext),
                supplied,
                StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<OAuthTransactionRecord?> ConsumeOAuthTransactionAsync(
        string state,
        CancellationToken cancellationToken)
    {
        var hash = Hash(state.Trim());
        return await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var record = await ReadOAuthTransactionAsync(connection, transaction, hash, cancellationToken);
            if (record is null) return null;
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM [LegendEngineeringChatGptPlanOAuthTransactions]
                WHERE [StateHash]=@state
                """;
            Add(delete, "@state", hash);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return record;
        }, cancellationToken);
    }

    private async Task MarkReauthorizationRequiredAsync(
        string leaseIdentity,
        CancellationToken cancellationToken)
    {
        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            var current = await ReadCredentialAsync(connection, transaction, cancellationToken);
            if (current is null ||
                !string.Equals(current.RefreshLeaseIdentity, leaseIdentity, StringComparison.Ordinal))
                return false;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [State]=@state,[RefreshLeaseIdentity]=NULL,[RefreshLeaseUntilUtc]=NULL,
                    [Revision]=@revision,[UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key
                """;
            Add(command, "@state", ReauthorizationRequired);
            Add(command, "@revision", Guid.NewGuid().ToString("N"));
            Add(command, "@updated", DateTime.UtcNow);
            Add(command, "@key", CredentialKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task ClearRefreshLeaseAsync(
        string leaseIdentity,
        CancellationToken cancellationToken)
    {
        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE [LegendEngineeringChatGptPlanCredentials]
                SET [RefreshLeaseIdentity]=NULL,[RefreshLeaseUntilUtc]=NULL,[UpdatedUtc]=@updated
                WHERE [CredentialKey]=@key AND [RefreshLeaseIdentity]=@lease
                """;
            Add(command, "@updated", DateTime.UtcNow);
            Add(command, "@key", CredentialKey);
            Add(command, "@lease", leaseIdentity);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<CredentialRecord?> ReadCredentialAsync(
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            return await ReadCredentialAsync(connection, null, cancellationToken);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private async Task<ClientRegistrationRecord?> ReadClientRegistrationAsync(
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            return await ReadClientRegistrationAsync(connection, null, cancellationToken);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static async Task<CredentialRecord?> ReadCredentialAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [ClientId],[AccessTokenCiphertext],[RefreshTokenCiphertext],[GrantedScopesJson],
                   [AccessTokenExpiresUtc],[State],[Revision],[RefreshLeaseIdentity],[RefreshLeaseUntilUtc],
                   [ConnectedUtc],[LastRefreshedUtc],[UpdatedUtc],
                   [ProviderBlockerClass],[ProviderBlockerCode],[ProviderBlockedUtc],[ProviderRetryNotBeforeUtc],
                   [ProviderRequestId],[ProviderHttpStatus],[ProviderErrorParam],[ProviderCircuitEpisodeId],
                   [ProviderRecoveredEpisodeId],[ProviderRecoveredUtc],[ProviderFailureStreak],
                   [ReadinessState],[ReadinessSignature],[ReadinessModelsJson],[ReadinessCheckedUtc],
                   [ReadinessResponseId],[ReadinessRequestId],[ReadinessCode],
                   [ProviderExecutionLeaseIdentity],[ProviderExecutionLeaseOwner],[ProviderExecutionLeaseUntilUtc]
            FROM [LegendEngineeringChatGptPlanCredentials]
            WHERE [CredentialKey]=@key
            """;
        Add(command, "@key", CredentialKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var scopes = JsonSerializer.Deserialize<string[]>(reader.GetString(3), JsonOptions) ?? [];
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            scopes,
            reader.GetDateTime(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetDateTime(14),
            reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt32(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetDateTime(21),
            reader.GetInt32(22),
            reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetDateTime(26),
            reader.IsDBNull(27) ? null : reader.GetString(27),
            reader.IsDBNull(28) ? null : reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : reader.GetString(30),
            reader.IsDBNull(31) ? null : reader.GetString(31),
            reader.IsDBNull(32) ? null : reader.GetDateTime(32));
    }

    private static async Task<ClientRegistrationRecord?> ReadClientRegistrationAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [ClientId],[AuthenticationMethod],[ClientSecretCiphertext],
                   [EligibilityConfirmed],[Revision],[UpdatedUtc]
            FROM [LegendEngineeringChatGptPlanClientRegistration]
            WHERE [RegistrationKey]=@key
            """;
        Add(command, "@key", RegistrationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetDateTime(5));
    }

    private static async Task<OAuthTransactionRecord?> ReadOAuthTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string stateHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [StateHash],[ClientId],[CodeVerifierCiphertext],[NonceCiphertext],
                   [RedirectUri],[CreatedUtc],[ExpiresUtc]
            FROM [LegendEngineeringChatGptPlanOAuthTransactions]
            WHERE [StateHash]=@state
            """;
        Add(command, "@state", stateHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDateTime(5),
            reader.GetDateTime(6));
    }

    private static async Task UpsertCredentialAsync(
        DbConnection connection,
        DbTransaction transaction,
        CredentialRecord credential,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE [LegendEngineeringChatGptPlanCredentials] SET
              [ClientId]=@client,[AccessTokenCiphertext]=@access,[RefreshTokenCiphertext]=@refresh,
              [GrantedScopesJson]=@scopes,[AccessTokenExpiresUtc]=@expires,[State]=@state,
              [Revision]=@revision,[RefreshLeaseIdentity]=@lease,[RefreshLeaseUntilUtc]=@leaseUntil,
              [ConnectedUtc]=@connected,[LastRefreshedUtc]=@refreshed,[UpdatedUtc]=@updated,
              [ProviderBlockerClass]=@blockerClass,[ProviderBlockerCode]=@blockerCode,
              [ProviderBlockedUtc]=@blockedUtc,[ProviderRetryNotBeforeUtc]=@retryUtc,
              [ProviderRequestId]=@providerRequest,[ProviderHttpStatus]=@providerStatus,
              [ProviderErrorParam]=@providerParam,[ProviderCircuitEpisodeId]=@episode,
              [ProviderRecoveredEpisodeId]=@recoveredEpisode,[ProviderRecoveredUtc]=@recoveredUtc,
              [ProviderFailureStreak]=@failureStreak,[ReadinessState]=@readinessState,
              [ReadinessSignature]=@readinessSignature,[ReadinessModelsJson]=@readinessModels,
              [ReadinessCheckedUtc]=@readinessChecked,[ReadinessResponseId]=@readinessResponse,
              [ReadinessRequestId]=@readinessRequest,[ReadinessCode]=@readinessCode,
              [ProviderExecutionLeaseIdentity]=@executionLease,
              [ProviderExecutionLeaseOwner]=@executionOwner,
              [ProviderExecutionLeaseUntilUtc]=@executionUntil
            WHERE [CredentialKey]=@key
            """;
        BindCredential(update, credential);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) return;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO [LegendEngineeringChatGptPlanCredentials]
              ([CredentialKey],[ClientId],[AccessTokenCiphertext],[RefreshTokenCiphertext],[GrantedScopesJson],
               [AccessTokenExpiresUtc],[State],[Revision],[RefreshLeaseIdentity],[RefreshLeaseUntilUtc],
               [ConnectedUtc],[LastRefreshedUtc],[UpdatedUtc],[ProviderBlockerClass],[ProviderBlockerCode],
               [ProviderBlockedUtc],[ProviderRetryNotBeforeUtc],[ProviderRequestId],[ProviderHttpStatus],
               [ProviderErrorParam],[ProviderCircuitEpisodeId],[ProviderRecoveredEpisodeId],[ProviderRecoveredUtc],
               [ProviderFailureStreak],[ReadinessState],[ReadinessSignature],[ReadinessModelsJson],
               [ReadinessCheckedUtc],[ReadinessResponseId],[ReadinessRequestId],[ReadinessCode],
               [ProviderExecutionLeaseIdentity],[ProviderExecutionLeaseOwner],[ProviderExecutionLeaseUntilUtc])
            VALUES (@key,@client,@access,@refresh,@scopes,@expires,@state,@revision,@lease,@leaseUntil,
                    @connected,@refreshed,@updated,@blockerClass,@blockerCode,@blockedUtc,@retryUtc,
                    @providerRequest,@providerStatus,@providerParam,@episode,@recoveredEpisode,@recoveredUtc,
                    @failureStreak,@readinessState,@readinessSignature,@readinessModels,@readinessChecked,
                    @readinessResponse,@readinessRequest,@readinessCode,@executionLease,@executionOwner,@executionUntil)
            """;
        BindCredential(insert, credential);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertClientRegistrationAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClientRegistrationRecord registration,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE [LegendEngineeringChatGptPlanClientRegistration] SET
              [ClientId]=@client,[AuthenticationMethod]=@method,[ClientSecretCiphertext]=@secret,
              [EligibilityConfirmed]=@confirmed,[Revision]=@revision,[UpdatedUtc]=@updated
            WHERE [RegistrationKey]=@key
            """;
        BindClientRegistration(update, registration);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 1) return;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO [LegendEngineeringChatGptPlanClientRegistration]
              ([RegistrationKey],[ClientId],[AuthenticationMethod],[ClientSecretCiphertext],
               [EligibilityConfirmed],[Revision],[UpdatedUtc])
            VALUES (@key,@client,@method,@secret,@confirmed,@revision,@updated)
            """;
        BindClientRegistration(insert, registration);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOAuthTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        OAuthTransactionRecord value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO [LegendEngineeringChatGptPlanOAuthTransactions]
              ([StateHash],[ClientId],[CodeVerifierCiphertext],[NonceCiphertext],
               [RedirectUri],[CreatedUtc],[ExpiresUtc])
            VALUES (@state,@client,@verifier,@nonce,@redirect,@created,@expires)
            """;
        Add(command, "@state", value.StateHash);
        Add(command, "@client", value.ClientId);
        Add(command, "@verifier", value.CodeVerifierCiphertext);
        Add(command, "@nonce", value.NonceCiphertext);
        Add(command, "@redirect", value.RedirectUri);
        Add(command, "@created", value.CreatedUtc);
        Add(command, "@expires", value.ExpiresUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCredentialAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM [LegendEngineeringChatGptPlanCredentials]
            WHERE [CredentialKey]=@key
            """;
        Add(command, "@key", CredentialKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteOAuthTransactionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM [LegendEngineeringChatGptPlanOAuthTransactions]";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExpiredOAuthTransactionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM [LegendEngineeringChatGptPlanOAuthTransactions]
            WHERE [ExpiresUtc] <= @now
            """;
        Add(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindCredential(DbCommand command, CredentialRecord credential)
    {
        Add(command, "@key", CredentialKey);
        Add(command, "@client", credential.ClientId);
        Add(command, "@access", credential.AccessTokenCiphertext);
        Add(command, "@refresh", credential.RefreshTokenCiphertext);
        Add(command, "@scopes", JsonSerializer.Serialize(credential.Scopes, JsonOptions));
        Add(command, "@expires", credential.ExpiresUtc);
        Add(command, "@state", credential.State);
        Add(command, "@revision", credential.Revision);
        Add(command, "@lease", credential.RefreshLeaseIdentity);
        Add(command, "@leaseUntil", credential.RefreshLeaseUntilUtc);
        Add(command, "@connected", credential.ConnectedUtc);
        Add(command, "@refreshed", credential.LastRefreshedUtc);
        Add(command, "@updated", credential.UpdatedUtc);
        Add(command, "@blockerClass", credential.ProviderBlockerClass);
        Add(command, "@blockerCode", credential.ProviderBlockerCode);
        Add(command, "@blockedUtc", credential.ProviderBlockedUtc);
        Add(command, "@retryUtc", credential.ProviderRetryNotBeforeUtc);
        Add(command, "@providerRequest", credential.ProviderRequestId);
        Add(command, "@providerStatus", credential.ProviderHttpStatus);
        Add(command, "@providerParam", credential.ProviderErrorParam);
        Add(command, "@episode", credential.ProviderCircuitEpisodeId);
        Add(command, "@recoveredEpisode", credential.ProviderRecoveredEpisodeId);
        Add(command, "@recoveredUtc", credential.ProviderRecoveredUtc);
        Add(command, "@failureStreak", credential.ProviderFailureStreak);
        Add(command, "@readinessState", credential.ReadinessState);
        Add(command, "@readinessSignature", credential.ReadinessSignature);
        Add(command, "@readinessModels", credential.ReadinessModelsJson);
        Add(command, "@readinessChecked", credential.ReadinessCheckedUtc);
        Add(command, "@readinessResponse", credential.ReadinessResponseId);
        Add(command, "@readinessRequest", credential.ReadinessRequestId);
        Add(command, "@readinessCode", credential.ReadinessCode);
        Add(command, "@executionLease", credential.ProviderExecutionLeaseIdentity);
        Add(command, "@executionOwner", credential.ProviderExecutionLeaseOwner);
        Add(command, "@executionUntil", credential.ProviderExecutionLeaseUntilUtc);
    }

    private static void BindClientRegistration(
        DbCommand command,
        ClientRegistrationRecord registration)
    {
        Add(command, "@key", RegistrationKey);
        Add(command, "@client", registration.ClientId);
        Add(command, "@method", registration.AuthenticationMethod);
        Add(command, "@secret", registration.ClientSecretCiphertext);
        Add(command, "@confirmed", registration.EligibilityConfirmed);
        Add(command, "@revision", registration.Revision);
        Add(command, "@updated", registration.UpdatedUtc);
    }

    private async Task<T> WithCredentialLockAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction =
                await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE [LegendEngineeringControlLocks]
                    SET [Revision]=[Revision]+1
                    WHERE [LockKey]='chatgpt-plan-credential'
                    """;
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("chatgpt_plan_credential_authority_unavailable");
            }

            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                try { await transaction.RollbackAsync(cancellationToken); } catch { }
                throw;
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private ChatGptPlanCredentialState Ready(
        CredentialRecord record,
        bool eligibilityConfirmed)
    {
        try
        {
            var access = _tokenProtector.Unprotect(record.AccessTokenCiphertext);
            IReadOnlyDictionary<string, string>? readinessModels = null;
            if (!string.IsNullOrWhiteSpace(record.ReadinessModelsJson))
            {
                try
                {
                    readinessModels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        record.ReadinessModelsJson, JsonOptions);
                }
                catch (JsonException) { }
            }
            return new(true, "chatgpt_plan_ready", record.ClientId, access,
                record.Scopes, record.ExpiresUtc, eligibilityConfirmed,
                record.ProviderBlockerClass, record.ProviderBlockerCode, record.ProviderBlockedUtc,
                record.ProviderRetryNotBeforeUtc, record.ProviderRequestId, record.ProviderHttpStatus,
                record.ProviderErrorParam, record.ProviderCircuitEpisodeId, record.ProviderRecoveredEpisodeId,
                record.ProviderRecoveredUtc, record.ProviderFailureStreak, record.ReadinessState,
                record.ReadinessSignature, readinessModels, record.ReadinessCheckedUtc,
                record.ReadinessResponseId, record.ReadinessRequestId, record.ReadinessCode);
        }
        catch (CryptographicException)
        {
            return new(false, "chatgpt_plan_reauthorization_required", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, eligibilityConfirmed);
        }
    }

    private static ChatGptPlanClientRegistrationState RegistrationState(
        ClientRegistrationRecord record) =>
        new(
            true,
            record.EligibilityConfirmed
                ? "chatgpt_plan_client_verified"
                : "chatgpt_plan_client_configured_unverified",
            record.ClientId,
            record.AuthenticationMethod,
            !string.IsNullOrWhiteSpace(record.ClientSecretCiphertext),
            record.EligibilityConfirmed);

    private static string NormalizeClientId(string? value)
    {
        var clientId = value?.Trim() ?? string.Empty;
        if (clientId.Length is < 8 or > 200 ||
            clientId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new InvalidOperationException("chatgpt_plan_client_registration_invalid");
        return clientId;
    }

    private static string NormalizeAuthenticationMethod(string? value)
    {
        var method = value?.Trim().ToLowerInvariant();
        return method switch
        {
            null or "" or "public" or AuthenticationNone => AuthenticationNone,
            "confidential" or AuthenticationBasic => AuthenticationBasic,
            _ => throw new InvalidOperationException("chatgpt_plan_client_authentication_method_invalid")
        };
    }

    private static string[] NormalizeScopes(IEnumerable<string> scopes) =>
        scopes.Where(scope => !string.IsNullOrWhiteSpace(scope) &&
                              scope.Length <= 120 &&
                              scope.All(character =>
                                  char.IsAsciiLetterOrDigit(character) ||
                                  character is '.' or ':' or '_' or '-'))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .Take(32)
            .ToArray();

    private static bool HasRequiredScopes(IEnumerable<string> scopes)
    {
        var set = new HashSet<string>(scopes, StringComparer.Ordinal);
        return set.Contains("offline_access") &&
               set.Contains("resource.invoke") &&
               set.Contains("chatgpt.tokens.use.direct");
    }

    private static bool ValidToken(string? value) =>
        value is { Length: >= 16 and <= 32_768 };

    private static bool ValidDigest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidModelBindingValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
                               character is '.' or '-' or '_' or '/' or ':');

    private static string? SafeControlValue(string? value, int maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum ||
            text.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                                    character is '.' or '-' or '_' or '/' or ':')))
            return null;
        return text;
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        string tooLargeCode,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException(tooLargeCode);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > maximumBytes)
                throw new InvalidOperationException(tooLargeCode);
            bytes.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static TokenResponse ParseTokenResponse(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var access = ReadString(root, "access_token") ?? string.Empty;
            var refresh = ReadString(root, "refresh_token") ?? string.Empty;
            var idToken = ReadString(root, "id_token");
            var expires = root.TryGetProperty("expires_in", out var expiry) &&
                          expiry.TryGetInt32(out var seconds)
                ? seconds
                : 0;
            var scopes = ReadString(root, "scope")
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            return new(access, refresh, idToken, expires, scopes);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("chatgpt_plan_authorization_response_invalid");
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return ReadString(json.RootElement, "error");
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RandomBase64Url(int bytes)
    {
        var value = new byte[bytes];
        RandomNumberGenerator.Fill(value);
        return WebEncoders.Base64UrlEncode(value);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ChatGptPlanCredentialState Blocked(
        string code,
        bool approved,
        string? clientId) =>
        new(false, code, clientId, null, [], null, approved);

    private sealed record CredentialRecord(
        string ClientId,
        string AccessTokenCiphertext,
        string RefreshTokenCiphertext,
        IReadOnlyList<string> Scopes,
        DateTime ExpiresUtc,
        string State,
        string Revision,
        string? RefreshLeaseIdentity,
        DateTime? RefreshLeaseUntilUtc,
        DateTime ConnectedUtc,
        DateTime? LastRefreshedUtc,
        DateTime UpdatedUtc,
        string? ProviderBlockerClass = null,
        string? ProviderBlockerCode = null,
        DateTime? ProviderBlockedUtc = null,
        DateTime? ProviderRetryNotBeforeUtc = null,
        string? ProviderRequestId = null,
        int? ProviderHttpStatus = null,
        string? ProviderErrorParam = null,
        string? ProviderCircuitEpisodeId = null,
        string? ProviderRecoveredEpisodeId = null,
        DateTime? ProviderRecoveredUtc = null,
        int ProviderFailureStreak = 0,
        string ReadinessState = "UNVERIFIED",
        string? ReadinessSignature = null,
        string? ReadinessModelsJson = null,
        DateTime? ReadinessCheckedUtc = null,
        string? ReadinessResponseId = null,
        string? ReadinessRequestId = null,
        string? ReadinessCode = null,
        string? ProviderExecutionLeaseIdentity = null,
        string? ProviderExecutionLeaseOwner = null,
        DateTime? ProviderExecutionLeaseUntilUtc = null);

    private sealed record ClientRegistrationRecord(
        string ClientId,
        string AuthenticationMethod,
        string? ClientSecretCiphertext,
        bool EligibilityConfirmed,
        string Revision,
        DateTime UpdatedUtc);

    private sealed record OAuthTransactionRecord(
        string StateHash,
        string ClientId,
        string CodeVerifierCiphertext,
        string NonceCiphertext,
        string RedirectUri,
        DateTime CreatedUtc,
        DateTime ExpiresUtc);

    private sealed record RefreshClaim(
        bool Claimed,
        string Code,
        CredentialRecord? Record);

    private sealed record TokenResponse(
        string AccessToken,
        string RefreshToken,
        string? IdToken,
        int ExpiresIn,
        IReadOnlyList<string> Scopes);

    private sealed record OpenAiDiscovery(
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string JwksUri,
        string? RevocationEndpoint,
        bool AuthorizationResponseIssuerSupported);

    private sealed class ChatGptPlanRefreshException(string code, bool terminal)
        : Exception(code)
    {
        internal string Code { get; } = code;
        internal bool Terminal { get; } = terminal;
    }
}
