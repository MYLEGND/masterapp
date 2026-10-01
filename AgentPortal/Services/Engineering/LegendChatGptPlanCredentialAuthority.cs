using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Engineering;

internal interface ILegendEngineeringAgentAdapter
{
    Task<object> GetStatusAsync(CancellationToken cancellationToken);
    Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken);
}

internal sealed record ChatGptPlanCredentialState(
    bool Ready,
    string Code,
    string? ClientId,
    string? AccessToken,
    IReadOnlyList<string> GrantedScopes,
    DateTime? ExpiresUtc,
    bool PrivateClientApproved);

internal interface ILegendChatGptPlanCredentialAuthority
{
    Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken);

    // Called only by the server-owned Founder OAuth completion path. Tokens never
    // enter a model/tool payload and are immediately protected before persistence.
    Task StoreAuthorizationAsync(
        string clientId,
        string accessToken,
        string refreshToken,
        IReadOnlyList<string> grantedScopes,
        DateTime accessTokenExpiresUtc,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal sealed class LegendChatGptPlanCredentialAuthority(
    MasterAppDbContext db,
    IConfiguration configuration,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClientFactory)
    : ILegendChatGptPlanCredentialAuthority
{
    private const string CredentialKey = "founder-chatgpt-plan";
    private const string Connected = "CONNECTED";
    private const string ReauthorizationRequired = "REAUTHORIZATION_REQUIRED";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector =
        dataProtection.CreateProtector("LEGEND.Engineering.ChatGptPlanCredential.v1");

    public async Task<ChatGptPlanCredentialState> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:PrivateClientApproved") != true)
            return Blocked("chatgpt_plan_private_client_eligibility_unverified", false);

        var configuredClientId = configuration["LegendEngineering:ChatGptPlan:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredClientId))
            return new(false, "chatgpt_plan_client_registration_missing", null, null, [], null, true);

        var record = await ReadCredentialAsync(cancellationToken);
        if (record is null)
        {
            if (!await TryImportConfiguredAuthorizationAsync(configuredClientId, cancellationToken))
                return new(false, "chatgpt_plan_sign_in_required", configuredClientId, null, [], null, true);
            record = await ReadCredentialAsync(cancellationToken);
        }

        if (record is null)
            return new(false, "chatgpt_plan_sign_in_required", configuredClientId, null, [], null, true);
        if (!string.Equals(record.ClientId, configuredClientId, StringComparison.Ordinal))
            return new(false, "chatgpt_plan_client_registration_changed", configuredClientId, null,
                record.Scopes, record.ExpiresUtc, true);
        if (!HasRequiredScopes(record.Scopes))
            return new(false, "chatgpt_plan_usage_scope_missing", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, true);
        if (!string.Equals(record.State, Connected, StringComparison.Ordinal))
            return new(false, "chatgpt_plan_reauthorization_required", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, true);

        if (record.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return Ready(record);

        return await RefreshAsync(record, cancellationToken);
    }

    public async Task StoreAuthorizationAsync(
        string clientId,
        string accessToken,
        string refreshToken,
        IReadOnlyList<string> grantedScopes,
        DateTime accessTokenExpiresUtc,
        CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:PrivateClientApproved") != true)
            throw new InvalidOperationException("chatgpt_plan_private_client_eligibility_unverified");

        var configuredClientId = configuration["LegendEngineering:ChatGptPlan:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredClientId) ||
            !string.Equals(clientId?.Trim(), configuredClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("chatgpt_plan_client_registration_mismatch");
        if (!ValidToken(accessToken) || !ValidToken(refreshToken) ||
            accessTokenExpiresUtc <= DateTime.UtcNow.AddMinutes(1))
            throw new InvalidOperationException("chatgpt_plan_authorization_invalid");

        var scopes = NormalizeScopes(grantedScopes);
        if (!HasRequiredScopes(scopes))
            throw new InvalidOperationException("chatgpt_plan_usage_scope_missing");

        var now = DateTime.UtcNow;
        var protectedRecord = new CredentialRecord(
            configuredClientId,
            _protector.Protect(accessToken.Trim()),
            _protector.Protect(refreshToken.Trim()),
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
            await UpsertCredentialAsync(connection, transaction, protectedRecord, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await WithCredentialLockAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM [LegendEngineeringChatGptPlanCredentials]
                WHERE [CredentialKey]=@key
                """;
            Add(command, "@key", CredentialKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<ChatGptPlanCredentialState> RefreshAsync(
        CredentialRecord snapshot,
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
                return Ready(claim.Record);
            return new(false, claim.Code, snapshot.ClientId, null, snapshot.Scopes,
                snapshot.ExpiresUtc, true);
        }

        string refreshToken;
        try
        {
            refreshToken = _protector.Unprotect(claim.Record!.RefreshTokenCiphertext);
        }
        catch (CryptographicException)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, true);
        }

        TokenRefreshResponse refreshed;
        try
        {
            refreshed = await RequestRefreshAsync(snapshot.ClientId, refreshToken, cancellationToken);
        }
        catch (ChatGptPlanRefreshException exception) when (!exception.Terminal)
        {
            await ClearRefreshLeaseAsync(leaseIdentity, cancellationToken);
            return new(false, exception.Code, snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, true);
        }
        catch (ChatGptPlanRefreshException)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, true);
        }

        if (!ValidToken(refreshed.AccessToken) || !ValidToken(refreshed.RefreshToken) ||
            refreshed.ExpiresIn < 60)
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_reauthorization_required", snapshot.ClientId, null,
                snapshot.Scopes, snapshot.ExpiresUtc, true);
        }

        var scopes = refreshed.Scopes.Count == 0
            ? snapshot.Scopes
            : NormalizeScopes(refreshed.Scopes);
        if (!HasRequiredScopes(scopes))
        {
            await MarkReauthorizationRequiredAsync(leaseIdentity, cancellationToken);
            return new(false, "chatgpt_plan_usage_scope_missing", snapshot.ClientId, null,
                scopes, null, true);
        }

        var now = DateTime.UtcNow;
        var stored = new CredentialRecord(
            snapshot.ClientId,
            _protector.Protect(refreshed.AccessToken.Trim()),
            _protector.Protect(refreshed.RefreshToken.Trim()),
            scopes,
            now.AddSeconds(refreshed.ExpiresIn),
            Connected,
            Guid.NewGuid().ToString("N"),
            null,
            null,
            snapshot.ConnectedUtc,
            now,
            now);

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
            ? new(true, "chatgpt_plan_ready", stored.ClientId, refreshed.AccessToken,
                stored.Scopes, stored.ExpiresUtc, true)
            : new(false, "chatgpt_plan_refresh_outcome_uncertain", stored.ClientId, null,
                stored.Scopes, stored.ExpiresUtc, true);
    }

    private async Task<TokenRefreshResponse> RequestRefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("LegendChatGptPlanOAuth");
        using var response = await client.PostAsync(
            "https://auth.openai.com/api/accounts/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["resource"] = "https://api.openai.com/v1"
            }),
            cancellationToken);

        var body = await ReadBoundedBodyAsync(response, 64 * 1024, cancellationToken);
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

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var access = ReadString(root, "access_token");
        var refresh = ReadString(root, "refresh_token");
        var expires = root.TryGetProperty("expires_in", out var expiry) &&
                      expiry.TryGetInt32(out var seconds)
            ? seconds
            : 0;
        var scopes = ReadString(root, "scope")
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
        return new(access ?? string.Empty, refresh ?? string.Empty, expires, scopes);
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
                   [ConnectedUtc],[LastRefreshedUtc],[UpdatedUtc]
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
            reader.GetDateTime(11));
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
              [ConnectedUtc]=@connected,[LastRefreshedUtc]=@refreshed,[UpdatedUtc]=@updated
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
               [ConnectedUtc],[LastRefreshedUtc],[UpdatedUtc])
            VALUES (@key,@client,@access,@refresh,@scopes,@expires,@state,@revision,@lease,@leaseUntil,
                    @connected,@refreshed,@updated)
            """;
        BindCredential(insert, credential);
        await insert.ExecuteNonQueryAsync(cancellationToken);
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

    private ChatGptPlanCredentialState Ready(CredentialRecord record)
    {
        try
        {
            var access = _protector.Unprotect(record.AccessTokenCiphertext);
            return new(true, "chatgpt_plan_ready", record.ClientId, access,
                record.Scopes, record.ExpiresUtc, true);
        }
        catch (CryptographicException)
        {
            return new(false, "chatgpt_plan_reauthorization_required", record.ClientId, null,
                record.Scopes, record.ExpiresUtc, true);
        }
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

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new ChatGptPlanRefreshException(
                "chatgpt_plan_refresh_response_too_large", terminal: true);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > maximumBytes)
                throw new ChatGptPlanRefreshException(
                    "chatgpt_plan_refresh_response_too_large", terminal: true);
            bytes.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
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

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ChatGptPlanCredentialState Blocked(string code, bool approved) =>
        new(false, code, null, null, [], null, approved);

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
        DateTime UpdatedUtc);

    private sealed record RefreshClaim(
        bool Claimed,
        string Code,
        CredentialRecord? Record);

    private sealed record TokenRefreshResponse(
        string AccessToken,
        string RefreshToken,
        int ExpiresIn,
        IReadOnlyList<string> Scopes);

    private sealed class ChatGptPlanRefreshException(string code, bool terminal)
        : Exception(code)
    {
        internal string Code { get; } = code;
        internal bool Terminal { get; } = terminal;
    }
}
