using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClientApp.Models;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClientApp.Services.QuickBooks;

public interface IQuickBooksIntegrationService
{
    bool IsConfigured { get; }
    string BuildAuthorizationUrl(string ownerUserId);
    Task<QuickBooksConnectResult> HandleCallbackAsync(string ownerUserId, string? state, string? code, string? realmId, CancellationToken ct);
    Task DisconnectAsync(string ownerUserId, CancellationToken ct);
    Task<QuickBooksDashboardData> GetDashboardAsync(string ownerUserId, bool forceRefresh, CancellationToken ct);
}

public sealed record QuickBooksConnectResult(bool Success, string Message);

public sealed class QuickBooksDashboardData
{
    public bool IsConnected { get; set; }
    public bool IsConfigured { get; set; }
    public string? RealmId { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }

    public decimal RevenueMtd { get; set; }
    public decimal RevenueYtd { get; set; }
    public decimal ExpensesMtd { get; set; }
    public decimal ExpensesYtd { get; set; }
    public decimal NetProfitMtd { get; set; }
    public decimal NetProfitYtd { get; set; }
    public decimal CashPosition { get; set; }
    public int AccountsCount { get; set; }

    public List<ExpenseCategoryVm> TopExpenseCategories { get; set; } = new();
    public List<ProfitTrendPointVm> ProfitTrend { get; set; } = new();
    public List<FinancialTransactionVm> RecentTransactions { get; set; } = new();
}

internal sealed class QuickBooksIntegrationService : IQuickBooksIntegrationService
{
    private const string OAuthGrantAuthorizationCode = "authorization_code";
    private const string OAuthGrantRefreshToken = "refresh_token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan OAuthStateTtl = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static bool _schemaEnsured;

    private readonly MasterAppDbContext _db;
    private readonly HttpClient _http;
    private readonly QuickBooksOptions _options;
    private readonly ILogger<QuickBooksIntegrationService> _logger;
    private readonly IDataProtector _tokenProtector;
    private readonly IDataProtector _stateProtector;

    public QuickBooksIntegrationService(
        MasterAppDbContext db,
        IHttpClientFactory httpClientFactory,
        IOptions<QuickBooksOptions> options,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<QuickBooksIntegrationService> logger)
    {
        _db = db;
        _http = httpClientFactory.CreateClient(nameof(QuickBooksIntegrationService));
        _options = options.Value;
        _logger = logger;
        _tokenProtector = dataProtectionProvider.CreateProtector("quickbooks-token-v1");
        _stateProtector = dataProtectionProvider.CreateProtector("quickbooks-oauth-state-v1");
    }

    public bool IsConfigured => _options.IsConfigured;

    public string BuildAuthorizationUrl(string ownerUserId)
    {
        if (!IsConfigured)
            return string.Empty;

        var payload = new OAuthStatePayload
        {
            OwnerUserId = ownerUserId,
            IssuedUtc = DateTime.UtcNow,
            Nonce = RandomNumberGenerator.GetHexString(16)
        };

        var raw = JsonSerializer.Serialize(payload, JsonOptions);
        var state = _stateProtector.Protect(raw);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["scope"] = _options.Scope,
            ["redirect_uri"] = _options.RedirectUri,
            ["state"] = state
        };

        var builder = new StringBuilder(_options.AuthorizationEndpoint);
        builder.Append('?');
        builder.Append(string.Join("&", query.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}")));
        return builder.ToString();
    }

    public async Task<QuickBooksConnectResult> HandleCallbackAsync(
        string ownerUserId,
        string? state,
        string? code,
        string? realmId,
        CancellationToken ct)
    {
        if (!IsConfigured)
            return new QuickBooksConnectResult(false, "QuickBooks is not configured.");

        if (!TryReadState(state, out var payload))
            return new QuickBooksConnectResult(false, "Invalid OAuth state.");

        if (!string.Equals(payload.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase))
            return new QuickBooksConnectResult(false, "OAuth state does not match the selected business client.");

        if (DateTime.UtcNow - payload.IssuedUtc > OAuthStateTtl)
            return new QuickBooksConnectResult(false, "OAuth state expired. Reconnect QuickBooks.");

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(realmId))
            return new QuickBooksConnectResult(false, "QuickBooks callback was missing authorization data.");

        await EnsureSchemaAsync(ct);

        var tokenResponse = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = OAuthGrantAuthorizationCode,
            ["code"] = code.Trim(),
            ["redirect_uri"] = _options.RedirectUri
        }, ct);

        if (!tokenResponse.Success)
            return new QuickBooksConnectResult(false, tokenResponse.ErrorMessage ?? "QuickBooks token exchange failed.");

        var connection = await _db.QuickBooksConnections
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId, ct);

        if (connection == null)
        {
            connection = new QuickBooksConnection
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId
            };
            _db.QuickBooksConnections.Add(connection);
        }

        connection.RealmId = realmId.Trim();
        connection.AccessTokenCipher = Protect(tokenResponse.AccessToken);
        connection.RefreshTokenCipher = Protect(tokenResponse.RefreshToken);
        connection.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(tokenResponse.AccessTokenExpiresIn);
        connection.RefreshTokenExpiresUtc = tokenResponse.RefreshTokenExpiresIn.HasValue
            ? DateTime.UtcNow.AddSeconds(tokenResponse.RefreshTokenExpiresIn.Value)
            : null;
        connection.IsActive = true;
        connection.LastSyncStatus = "connected";
        connection.LastSyncError = null;
        connection.UpdatedUtc = DateTime.UtcNow;
        connection.ConnectedUtc = connection.ConnectedUtc == default ? DateTime.UtcNow : connection.ConnectedUtc;

        await _db.SaveChangesAsync(ct);

        try
        {
            await RefreshSnapshotAsync(connection, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickBooks initial snapshot refresh failed for owner={OwnerUserId}", ownerUserId);
        }

        return new QuickBooksConnectResult(true, "QuickBooks connected.");
    }

    public async Task DisconnectAsync(string ownerUserId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var connection = await _db.QuickBooksConnections
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId, ct);

        if (connection != null)
        {
            connection.IsActive = false;
            connection.AccessTokenCipher = string.Empty;
            connection.RefreshTokenCipher = string.Empty;
            connection.AccessTokenExpiresUtc = DateTime.UtcNow;
            connection.RefreshTokenExpiresUtc = null;
            connection.LastSyncStatus = "disconnected";
            connection.LastSyncError = null;
            connection.UpdatedUtc = DateTime.UtcNow;
        }

        var snapshot = await _db.QuickBooksFinancialSnapshots
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId, ct);

        if (snapshot != null)
            _db.QuickBooksFinancialSnapshots.Remove(snapshot);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<QuickBooksDashboardData> GetDashboardAsync(string ownerUserId, bool forceRefresh, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        var data = new QuickBooksDashboardData
        {
            IsConfigured = IsConfigured
        };

        if (!IsConfigured)
            return data;

        var connection = await _db.QuickBooksConnections
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId && x.IsActive, ct);

        if (connection == null)
            return data;

        data.IsConnected = true;
        data.RealmId = connection.RealmId;
        data.LastSyncedUtc = connection.LastSyncUtc;
        data.LastSyncStatus = connection.LastSyncStatus;
        data.LastSyncError = connection.LastSyncError;

        var snapshot = await _db.QuickBooksFinancialSnapshots
            .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId, ct);

        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.SnapshotTtlMinutes));
        var shouldRefresh = forceRefresh ||
            snapshot == null ||
            DateTime.UtcNow - snapshot.SyncedUtc > ttl;

        if (shouldRefresh)
        {
            try
            {
                snapshot = await RefreshSnapshotAsync(connection, ct);
            }
            catch (Exception ex)
            {
                connection.LastSyncStatus = "error";
                connection.LastSyncError = Truncate(ex.Message, 1000);
                connection.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogError(ex, "QuickBooks snapshot refresh failed for owner={OwnerUserId}", ownerUserId);

                snapshot ??= await _db.QuickBooksFinancialSnapshots
                    .SingleOrDefaultAsync(x => x.OwnerUserId == ownerUserId, ct);
            }
        }

        if (snapshot == null)
            return data;

        data.RevenueMtd = snapshot.RevenueMtd;
        data.RevenueYtd = snapshot.RevenueYtd;
        data.ExpensesMtd = snapshot.ExpensesMtd;
        data.ExpensesYtd = snapshot.ExpensesYtd;
        data.NetProfitMtd = snapshot.NetProfitMtd;
        data.NetProfitYtd = snapshot.NetProfitYtd;
        data.CashPosition = snapshot.CashPosition;
        data.AccountsCount = snapshot.AccountsCount;
        data.TopExpenseCategories = DeserializeList<ExpenseCategoryVm>(snapshot.TopExpenseCategoriesJson);
        data.ProfitTrend = DeserializeList<ProfitTrendPointVm>(snapshot.ProfitTrendJson);
        data.RecentTransactions = DeserializeList<FinancialTransactionVm>(snapshot.RecentTransactionsJson);
        data.LastSyncedUtc = snapshot.SyncedUtc;

        return data;
    }

    private async Task<QuickBooksFinancialSnapshot> RefreshSnapshotAsync(QuickBooksConnection connection, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        var accessToken = await EnsureAccessTokenAsync(connection, ct);

        using var pnlMtd = await GetQuickBooksJsonAsync(connection, accessToken,
            $"reports/ProfitAndLoss?start_date={monthStart:yyyy-MM-dd}&end_date={today:yyyy-MM-dd}", ct);
        using var pnlYtd = await GetQuickBooksJsonAsync(connection, accessToken,
            $"reports/ProfitAndLoss?start_date={yearStart:yyyy-MM-dd}&end_date={today:yyyy-MM-dd}", ct);
        using var pnlTrend = await GetQuickBooksJsonAsync(connection, accessToken,
            $"reports/ProfitAndLoss?start_date={yearStart:yyyy-MM-dd}&end_date={today:yyyy-MM-dd}&summarize_column_by=Month", ct);
        using var balanceSheet = await GetQuickBooksJsonAsync(connection, accessToken,
            $"reports/BalanceSheet?as_of_date={today:yyyy-MM-dd}", ct);
        using var accounts = await GetQuickBooksJsonAsync(connection, accessToken,
            "query?query=select%20*%20from%20Account%20startposition%201%20maxresults%201000", ct);
        using var transactions = await GetQuickBooksJsonAsync(connection, accessToken,
            $"reports/TransactionList?start_date={monthStart:yyyy-MM-dd}&end_date={today:yyyy-MM-dd}", ct);

        var revenueMtd = ParseProfitAndLossMetric(pnlMtd.RootElement, "Total Income");
        var expensesMtd = ParseProfitAndLossMetric(pnlMtd.RootElement, "Total Expenses");
        var netMtd = ParseProfitAndLossMetric(pnlMtd.RootElement, "Net Income", "Net Operating Income");

        var revenueYtd = ParseProfitAndLossMetric(pnlYtd.RootElement, "Total Income");
        var expensesYtd = ParseProfitAndLossMetric(pnlYtd.RootElement, "Total Expenses");
        var netYtd = ParseProfitAndLossMetric(pnlYtd.RootElement, "Net Income", "Net Operating Income");

        var cashPosition = ParseCashPosition(balanceSheet.RootElement);
        var accountsCount = ParseAccountsCount(accounts.RootElement);
        var topCategories = ParseTopExpenseCategories(pnlYtd.RootElement);
        var trend = ParseProfitTrend(pnlTrend.RootElement);
        var recent = ParseRecentTransactions(transactions.RootElement);

        var snapshot = await _db.QuickBooksFinancialSnapshots
            .SingleOrDefaultAsync(x => x.OwnerUserId == connection.OwnerUserId, ct);

        if (snapshot == null)
        {
            snapshot = new QuickBooksFinancialSnapshot
            {
                Id = Guid.NewGuid(),
                OwnerUserId = connection.OwnerUserId
            };
            _db.QuickBooksFinancialSnapshots.Add(snapshot);
        }

        snapshot.RealmId = connection.RealmId;
        snapshot.SyncedUtc = DateTime.UtcNow;
        snapshot.RevenueMtd = revenueMtd;
        snapshot.RevenueYtd = revenueYtd;
        snapshot.ExpensesMtd = expensesMtd;
        snapshot.ExpensesYtd = expensesYtd;
        snapshot.NetProfitMtd = netMtd;
        snapshot.NetProfitYtd = netYtd;
        snapshot.CashPosition = cashPosition;
        snapshot.AccountsCount = accountsCount;
        snapshot.SourceTag = "quickbooks_cache";
        snapshot.TopExpenseCategoriesJson = JsonSerializer.Serialize(topCategories, JsonOptions);
        snapshot.ProfitTrendJson = JsonSerializer.Serialize(trend, JsonOptions);
        snapshot.RecentTransactionsJson = JsonSerializer.Serialize(recent, JsonOptions);

        connection.LastSyncUtc = snapshot.SyncedUtc;
        connection.LastSyncStatus = "ok";
        connection.LastSyncError = null;
        connection.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return snapshot;
    }

    private async Task<string> EnsureAccessTokenAsync(QuickBooksConnection connection, CancellationToken ct)
    {
        if (connection.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
            return Unprotect(connection.AccessTokenCipher);

        var refreshToken = Unprotect(connection.RefreshTokenCipher);
        var refreshed = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = OAuthGrantRefreshToken,
            ["refresh_token"] = refreshToken
        }, ct);

        if (!refreshed.Success)
            throw new InvalidOperationException(refreshed.ErrorMessage ?? "QuickBooks token refresh failed.");

        connection.AccessTokenCipher = Protect(refreshed.AccessToken);
        connection.RefreshTokenCipher = Protect(refreshed.RefreshToken);
        connection.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(refreshed.AccessTokenExpiresIn);
        connection.RefreshTokenExpiresUtc = refreshed.RefreshTokenExpiresIn.HasValue
            ? DateTime.UtcNow.AddSeconds(refreshed.RefreshTokenExpiresIn.Value)
            : connection.RefreshTokenExpiresUtc;
        connection.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return refreshed.AccessToken;
    }

    private async Task<JsonDocument> GetQuickBooksJsonAsync(
        QuickBooksConnection connection,
        string accessToken,
        string relativePath,
        CancellationToken ct)
    {
        var response = await SendQuickBooksGetAsync(connection.RealmId, accessToken, relativePath, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshed = await EnsureAccessTokenAsync(connection, ct);
            response.Dispose();
            response = await SendQuickBooksGetAsync(connection.RealmId, refreshed, relativePath, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"QuickBooks API error ({(int)response.StatusCode}): {Truncate(body, 900)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendQuickBooksGetAsync(
        string realmId,
        string accessToken,
        string relativePath,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/{realmId}/{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<TokenExchangeResult> SendTokenRequestAsync(Dictionary<string, string> formFields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint);
        request.Content = new FormUrlEncodedContent(formFields);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var msg = $"QuickBooks OAuth error ({(int)response.StatusCode}): {Truncate(responseBody, 700)}";
            return TokenExchangeResult.Fail(msg);
        }

        using var json = JsonDocument.Parse(responseBody);
        if (!json.RootElement.TryGetProperty("access_token", out var accessTokenEl) ||
            !json.RootElement.TryGetProperty("refresh_token", out var refreshTokenEl))
        {
            return TokenExchangeResult.Fail("QuickBooks OAuth response missing access or refresh token.");
        }

        var accessToken = accessTokenEl.GetString() ?? "";
        var refreshToken = refreshTokenEl.GetString() ?? "";
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;
        var refreshExp = json.RootElement.TryGetProperty("x_refresh_token_expires_in", out var refreshExpEl)
            ? refreshExpEl.GetInt32()
            : (int?)null;

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            return TokenExchangeResult.Fail("QuickBooks OAuth response returned empty tokens.");

        return TokenExchangeResult.Ok(accessToken, refreshToken, expiresIn, refreshExp);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaEnsured) return;

        await SchemaLock.WaitAsync(ct);
        try
        {
            if (_schemaEnsured) return;

            if (_db.Database.IsSqlite())
            {
                const string sqliteSql = """
CREATE TABLE IF NOT EXISTS "QuickBooksConnections" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_QuickBooksConnections" PRIMARY KEY,
    "OwnerUserId" TEXT NOT NULL,
    "RealmId" TEXT NOT NULL,
    "AccessTokenCipher" TEXT NOT NULL,
    "RefreshTokenCipher" TEXT NOT NULL,
    "AccessTokenExpiresUtc" TEXT NOT NULL,
    "RefreshTokenExpiresUtc" TEXT NULL,
    "ConnectedUtc" TEXT NOT NULL,
    "UpdatedUtc" TEXT NOT NULL,
    "LastSyncUtc" TEXT NULL,
    "LastSyncStatus" TEXT NULL,
    "LastSyncError" TEXT NULL,
    "IsActive" INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_QuickBooksConnections_OwnerUserId" ON "QuickBooksConnections" ("OwnerUserId");
CREATE INDEX IF NOT EXISTS "IX_QuickBooksConnections_IsActive" ON "QuickBooksConnections" ("IsActive");

CREATE TABLE IF NOT EXISTS "QuickBooksFinancialSnapshots" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_QuickBooksFinancialSnapshots" PRIMARY KEY,
    "OwnerUserId" TEXT NOT NULL,
    "RealmId" TEXT NOT NULL,
    "SyncedUtc" TEXT NOT NULL,
    "RevenueMtd" decimal(18,2) NOT NULL,
    "RevenueYtd" decimal(18,2) NOT NULL,
    "ExpensesMtd" decimal(18,2) NOT NULL,
    "ExpensesYtd" decimal(18,2) NOT NULL,
    "NetProfitMtd" decimal(18,2) NOT NULL,
    "NetProfitYtd" decimal(18,2) NOT NULL,
    "CashPosition" decimal(18,2) NOT NULL,
    "SourceTag" TEXT NOT NULL,
    "AccountsCount" INTEGER NOT NULL DEFAULT 0,
    "TopExpenseCategoriesJson" TEXT NULL,
    "ProfitTrendJson" TEXT NULL,
    "RecentTransactionsJson" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_QuickBooksFinancialSnapshots_OwnerUserId" ON "QuickBooksFinancialSnapshots" ("OwnerUserId");
CREATE INDEX IF NOT EXISTS "IX_QuickBooksFinancialSnapshots_SyncedUtc" ON "QuickBooksFinancialSnapshots" ("SyncedUtc");
""";
                await _db.Database.ExecuteSqlRawAsync(sqliteSql, ct);
            }
            else if (_db.Database.IsSqlServer())
            {
                const string sqlServerSql = """
IF OBJECT_ID(N'[dbo].[QuickBooksConnections]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[QuickBooksConnections](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_QuickBooksConnections] PRIMARY KEY,
        [OwnerUserId] nvarchar(450) NOT NULL,
        [RealmId] nvarchar(128) NOT NULL,
        [AccessTokenCipher] nvarchar(max) NOT NULL,
        [RefreshTokenCipher] nvarchar(max) NOT NULL,
        [AccessTokenExpiresUtc] datetime2 NOT NULL,
        [RefreshTokenExpiresUtc] datetime2 NULL,
        [ConnectedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        [LastSyncUtc] datetime2 NULL,
        [LastSyncStatus] nvarchar(64) NULL,
        [LastSyncError] nvarchar(1000) NULL,
        [IsActive] bit NOT NULL
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksConnections_OwnerUserId' AND object_id = OBJECT_ID(N'[dbo].[QuickBooksConnections]'))
    CREATE UNIQUE INDEX [IX_QuickBooksConnections_OwnerUserId] ON [dbo].[QuickBooksConnections]([OwnerUserId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksConnections_IsActive' AND object_id = OBJECT_ID(N'[dbo].[QuickBooksConnections]'))
    CREATE INDEX [IX_QuickBooksConnections_IsActive] ON [dbo].[QuickBooksConnections]([IsActive]);

IF OBJECT_ID(N'[dbo].[QuickBooksFinancialSnapshots]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[QuickBooksFinancialSnapshots](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_QuickBooksFinancialSnapshots] PRIMARY KEY,
        [OwnerUserId] nvarchar(450) NOT NULL,
        [RealmId] nvarchar(128) NOT NULL,
        [SyncedUtc] datetime2 NOT NULL,
        [RevenueMtd] decimal(18,2) NOT NULL,
        [RevenueYtd] decimal(18,2) NOT NULL,
        [ExpensesMtd] decimal(18,2) NOT NULL,
        [ExpensesYtd] decimal(18,2) NOT NULL,
        [NetProfitMtd] decimal(18,2) NOT NULL,
        [NetProfitYtd] decimal(18,2) NOT NULL,
        [CashPosition] decimal(18,2) NOT NULL,
        [SourceTag] nvarchar(64) NOT NULL,
        [AccountsCount] int NOT NULL,
        [TopExpenseCategoriesJson] nvarchar(max) NULL,
        [ProfitTrendJson] nvarchar(max) NULL,
        [RecentTransactionsJson] nvarchar(max) NULL
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksFinancialSnapshots_OwnerUserId' AND object_id = OBJECT_ID(N'[dbo].[QuickBooksFinancialSnapshots]'))
    CREATE UNIQUE INDEX [IX_QuickBooksFinancialSnapshots_OwnerUserId] ON [dbo].[QuickBooksFinancialSnapshots]([OwnerUserId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksFinancialSnapshots_SyncedUtc' AND object_id = OBJECT_ID(N'[dbo].[QuickBooksFinancialSnapshots]'))
    CREATE INDEX [IX_QuickBooksFinancialSnapshots_SyncedUtc] ON [dbo].[QuickBooksFinancialSnapshots]([SyncedUtc]);
""";
                await _db.Database.ExecuteSqlRawAsync(sqlServerSql, ct);
            }

            _schemaEnsured = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    private bool TryReadState(string? state, out OAuthStatePayload payload)
    {
        payload = new OAuthStatePayload();
        if (string.IsNullOrWhiteSpace(state)) return false;

        try
        {
            var json = _stateProtector.Unprotect(state);
            var parsed = JsonSerializer.Deserialize<OAuthStatePayload>(json, JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.OwnerUserId)) return false;
            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string Protect(string value) => _tokenProtector.Protect(value);

    private string Unprotect(string cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
            return string.Empty;

        try { return _tokenProtector.Unprotect(cipher); }
        catch { return string.Empty; }
    }

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static decimal ParseProfitAndLossMetric(JsonElement root, params string[] labels)
    {
        foreach (var row in EnumerateReportRows(root))
        {
            if (!TryGetRowLabel(row, out var label)) continue;
            if (!labels.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase))) continue;
            if (TryGetLastNumericFromRow(row, out var value))
                return value;
        }

        return 0m;
    }

    private static decimal ParseCashPosition(JsonElement root)
    {
        var labels = new[]
        {
            "Total Cash and cash equivalents",
            "Cash and cash equivalents",
            "Total Bank Accounts",
            "Cash"
        };

        foreach (var row in EnumerateReportRows(root))
        {
            if (!TryGetRowLabel(row, out var label)) continue;
            if (!labels.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase))) continue;
            if (TryGetLastNumericFromRow(row, out var value))
                return value;
        }

        return 0m;
    }

    private static int ParseAccountsCount(JsonElement root)
    {
        if (!root.TryGetProperty("QueryResponse", out var queryResponse)) return 0;
        if (!queryResponse.TryGetProperty("Account", out var accounts)) return 0;
        return accounts.ValueKind == JsonValueKind.Array ? accounts.GetArrayLength() : 0;
    }

    private static List<ExpenseCategoryVm> ParseTopExpenseCategories(JsonElement root)
    {
        var output = new List<ExpenseCategoryVm>();
        JsonElement expenseSection = default;
        var found = false;

        foreach (var row in EnumerateReportRows(root))
        {
            if (!TryGetRowLabel(row, out var label)) continue;
            if (!string.Equals(label, "Expenses", StringComparison.OrdinalIgnoreCase)) continue;
            if (!row.TryGetProperty("Rows", out expenseSection)) continue;
            found = true;
            break;
        }

        if (!found || !expenseSection.TryGetProperty("Row", out var expenseRows) || expenseRows.ValueKind != JsonValueKind.Array)
            return output;

        foreach (var item in expenseRows.EnumerateArray())
        {
            if (!TryGetRowLabel(item, out var label)) continue;
            if (string.IsNullOrWhiteSpace(label)) continue;
            if (label.StartsWith("Total ", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryGetLastNumericFromRow(item, out var amount)) continue;
            if (amount <= 0m) continue;

            output.Add(new ExpenseCategoryVm
            {
                Name = label,
                Amount = amount
            });
        }

        return output
            .OrderByDescending(x => x.Amount)
            .Take(6)
            .ToList();
    }

    private static List<ProfitTrendPointVm> ParseProfitTrend(JsonElement root)
    {
        var points = new List<ProfitTrendPointVm>();

        if (!root.TryGetProperty("Columns", out var columns) ||
            !columns.TryGetProperty("Column", out var columnList) ||
            columnList.ValueKind != JsonValueKind.Array)
            return points;

        var monthKeys = new List<string>();
        foreach (var col in columnList.EnumerateArray().Skip(1))
        {
            var maybeMonth = ExtractColumnMonthKey(col);
            if (!string.IsNullOrWhiteSpace(maybeMonth))
                monthKeys.Add(maybeMonth);
        }

        if (monthKeys.Count == 0)
            return points;

        JsonElement? netIncomeRow = null;
        foreach (var row in EnumerateReportRows(root))
        {
            if (!TryGetRowLabel(row, out var label)) continue;
            if (string.Equals(label, "Net Income", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Net Operating Income", StringComparison.OrdinalIgnoreCase))
            {
                netIncomeRow = row;
                break;
            }
        }

        if (!netIncomeRow.HasValue || !netIncomeRow.Value.TryGetProperty("ColData", out var colData) || colData.ValueKind != JsonValueKind.Array)
            return points;

        var values = colData.EnumerateArray()
            .Skip(1)
            .Select(TryReadDecimal)
            .ToList();

        var count = Math.Min(monthKeys.Count, values.Count);
        for (var i = 0; i < count; i++)
        {
            points.Add(new ProfitTrendPointVm
            {
                MonthKey = monthKeys[i],
                NetProfit = values[i]
            });
        }

        return points;
    }

    private static List<FinancialTransactionVm> ParseRecentTransactions(JsonElement root)
    {
        var output = new List<FinancialTransactionVm>();

        foreach (var row in EnumerateReportRows(root))
        {
            if (!row.TryGetProperty("ColData", out var colData) || colData.ValueKind != JsonValueKind.Array)
                continue;

            var values = colData.EnumerateArray()
                .Select(c => c.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "")
                .ToList();

            if (values.Count == 0) continue;

            if (!DateTime.TryParse(values[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                continue;

            var amount = values.Count > 0 ? values.Select(v => TryParseDecimal(v)).LastOrDefault() : 0m;

            output.Add(new FinancialTransactionVm
            {
                Date = date,
                Type = values.Count > 1 ? values[1] : "",
                Name = values.Count > 3 ? values[3] : (values.Count > 2 ? values[2] : ""),
                Account = values.Count > 4 ? values[4] : "",
                Amount = amount
            });
        }

        return output
            .OrderByDescending(x => x.Date)
            .Take(30)
            .ToList();
    }

    private static IEnumerable<JsonElement> EnumerateReportRows(JsonElement root)
    {
        if (!root.TryGetProperty("Rows", out var rows)) yield break;
        if (!rows.TryGetProperty("Row", out var rowList) || rowList.ValueKind != JsonValueKind.Array) yield break;

        foreach (var row in rowList.EnumerateArray())
        {
            foreach (var nested in EnumerateRowRecursive(row))
                yield return nested;
        }
    }

    private static IEnumerable<JsonElement> EnumerateRowRecursive(JsonElement row)
    {
        yield return row;

        if (row.TryGetProperty("Rows", out var childRows) &&
            childRows.TryGetProperty("Row", out var rowList) &&
            rowList.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in rowList.EnumerateArray())
            {
                foreach (var nested in EnumerateRowRecursive(child))
                    yield return nested;
            }
        }
    }

    private static bool TryGetRowLabel(JsonElement row, out string label)
    {
        label = "";

        if (!row.TryGetProperty("ColData", out var colData) || colData.ValueKind != JsonValueKind.Array)
            return false;

        var first = colData.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return false;
        if (!first.TryGetProperty("value", out var valueEl)) return false;
        label = valueEl.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(label);
    }

    private static bool TryGetLastNumericFromRow(JsonElement row, out decimal value)
    {
        value = 0m;
        if (!row.TryGetProperty("ColData", out var colData) || colData.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in colData.EnumerateArray().Reverse())
        {
            if (!entry.TryGetProperty("value", out var raw)) continue;
            var parsed = TryParseDecimal(raw.GetString());
            if (parsed == 0m) continue;
            value = parsed;
            return true;
        }

        return false;
    }

    private static string ExtractColumnMonthKey(JsonElement column)
    {
        if (column.TryGetProperty("MetaData", out var md) &&
            md.TryGetProperty("Name", out var nameEl))
        {
            var raw = (nameEl.GetString() ?? "").Trim();
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                return dt.ToString("yyyy-MM");

            if (DateTime.TryParseExact(raw, "MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt2))
                return dt2.ToString("yyyy-MM");
        }

        if (column.TryGetProperty("ColTitle", out var titleEl))
        {
            var title = (titleEl.GetString() ?? "").Trim();
            if (DateTime.TryParse(title, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                return dt.ToString("yyyy-MM");
        }

        return "";
    }

    private static decimal TryReadDecimal(JsonElement colDataEntry)
    {
        if (!colDataEntry.TryGetProperty("value", out var valueEl))
            return 0m;
        return TryParseDecimal(valueEl.GetString());
    }

    private static decimal TryParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0m;

        var cleaned = raw
            .Replace(",", "")
            .Replace("$", "")
            .Trim();

        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= maxLen ? value : value[..maxLen];
    }

    private sealed class OAuthStatePayload
    {
        public string OwnerUserId { get; set; } = "";
        public DateTime IssuedUtc { get; set; }
        public string Nonce { get; set; } = "";
    }

    private sealed class TokenExchangeResult
    {
        public bool Success { get; private init; }
        public string AccessToken { get; private init; } = "";
        public string RefreshToken { get; private init; } = "";
        public int AccessTokenExpiresIn { get; private init; }
        public int? RefreshTokenExpiresIn { get; private init; }
        public string? ErrorMessage { get; private init; }

        public static TokenExchangeResult Ok(string accessToken, string refreshToken, int accessTokenExpiresIn, int? refreshTokenExpiresIn)
            => new()
            {
                Success = true,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessTokenExpiresIn = accessTokenExpiresIn,
                RefreshTokenExpiresIn = refreshTokenExpiresIn
            };

        public static TokenExchangeResult Fail(string message)
            => new()
            {
                Success = false,
                ErrorMessage = message
            };
    }
}
