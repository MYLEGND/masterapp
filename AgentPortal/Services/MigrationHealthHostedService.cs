using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentPortal.Services;

/// <summary>
/// Lightweight startup diagnostics to surface migration drift across providers.
/// Validates migrations plus the schema required by core execution, mobile social,
/// and subscription surfaces before the portal accepts traffic.
/// No schema changes are applied here.
/// </summary>
public sealed class MigrationHealthHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MigrationHealthHostedService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private int _hasValidated;

    private static readonly string[] CriticalTables =
    {
        "ActionItems", "ActionLogs", "Blockers", "DecisionRecords", "Commitments", "AnalyticsEvents",
        "SocialPosts", "SocialPostMediaAssets", "SocialPostComments", "SocialPostReactions",
        "SocialPostMusicAttachments", "SocialPostReposts", "SocialPostSaves", "SocialPostShares",
        "SocialPostViews", "SocialProfileVisits", "ClientSubscriptionOffers", "ClientSubscriptions"
    };

    private static readonly CriticalColumn[] CriticalColumns =
    {
        new("ClientSubscriptionOffers", "FreeTrialDays"),
        new("ClientSubscriptions", "TrialEndsUtc")
    };

    public MigrationHealthHostedService(
        IServiceProvider services,
        ILogger<MigrationHealthHostedService> logger,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        EnsureSchemaReadyAsync(cancellationToken);

    public async Task EnsureSchemaReadyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _hasValidated) == 1)
        {
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        var provider = db.Database.ProviderName ?? "unknown";
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            _logger.LogInformation("DB provider {Provider}; pending migrations: {PendingCount}", provider, pending.Length);

            if (pending.Length > 0)
            {
                throw new DatabaseSchemaDriftException(
                    $"STARTUP BLOCKED: {pending.Length} database migration(s) are pending. " +
                    $"Apply migrations before starting the portal. First: {pending[0]}");
            }

            if (db.Database.IsSqlite())
            {
                var dataSource = db.Database.GetDbConnection().DataSource;
                _logger.LogInformation("SQLite data source: {DataSource}", dataSource);
            }

            var missingTables = new List<string>();
            foreach (var table in CriticalTables)
            {
                var exists = await TableExistsAsync(db, table, cancellationToken);
                if (!exists)
                {
                    missingTables.Add(table);
                    _logger.LogWarning("Critical table missing: {Table}. Apply migrations for provider {Provider}.", table, provider);
                }
            }

            if (missingTables.Count > 0)
            {
                throw new DatabaseSchemaDriftException(
                    $"STARTUP BLOCKED: Required database tables are missing: {string.Join(", ", missingTables)}. " +
                    $"Apply migrations for provider {provider} before starting the application.");
            }

            var missingColumns = new List<string>();
            foreach (var column in CriticalColumns)
            {
                var exists = await ColumnExistsAsync(db, column, cancellationToken);
                if (!exists)
                {
                    var name = $"{column.Table}.{column.Name}";
                    missingColumns.Add(name);
                    _logger.LogWarning("Critical column missing: {Column}. Apply migrations for provider {Provider}.", name, provider);
                }
            }

            if (missingColumns.Count > 0)
            {
                throw new DatabaseSchemaDriftException(
                    $"STARTUP BLOCKED: Required database columns are missing: {string.Join(", ", missingColumns)}. " +
                    $"Apply migrations for provider {provider} before starting the application.");
            }
        }
        catch (DatabaseSchemaDriftException)
        {
            throw;
        }
        catch (Exception ex) when (ShouldSuppressStartupFailure(db, ex))
        {
            _logger.LogWarning(
                ex,
                "Migration health check skipped during local development because the configured database is currently unreachable. " +
                "The app will continue starting in non-strict mode.");
        }

        Interlocked.Exchange(ref _hasValidated, 1);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool ShouldSuppressStartupFailure(MasterAppDbContext db, Exception ex)
    {
        if (!_environment.IsDevelopment() || IsStrictMigrationsEnabled())
        {
            return false;
        }

        if (!db.Database.IsSqlServer())
        {
            return false;
        }

        return ex is DbException or TimeoutException or InvalidOperationException || ex.InnerException is DbException;
    }

    private bool IsStrictMigrationsEnabled()
    {
        return string.Equals(_configuration["Migrations:Strict"], "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("MIGRATION_STRICT"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TableExistsAsync(
        MasterAppDbContext db,
        string table,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                return false;
            }

            var conn = db.Database.GetDbConnection();

            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
            }

            await using var cmd = conn.CreateCommand();

            if (db.Database.IsSqlite())
            {
                cmd.CommandText = @"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table'
AND name = $table";

                var param = cmd.CreateParameter();
                param.ParameterName = "$table";
                param.Value = table;
                cmd.Parameters.Add(param);
            }
            else
            {
                cmd.CommandText = @"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = @table";

                var param = cmd.CreateParameter();
                param.ParameterName = "@table";
                param.Value = table;
                cmd.Parameters.Add(param);
            }

            var result = await cmd.ExecuteScalarAsync(ct);

            return Convert.ToInt32(result) > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Table existence check failed for {Table}", table);
            return false;
        }
    }

    private async Task<bool> ColumnExistsAsync(
        MasterAppDbContext db,
        CriticalColumn column,
        CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            if (db.Database.IsSqlite())
            {
                cmd.CommandText = @"
SELECT COUNT(1)
FROM pragma_table_info($table)
WHERE name = $column";

                AddParameter(cmd, "$table", column.Table);
                AddParameter(cmd, "$column", column.Name);
            }
            else
            {
                cmd.CommandText = @"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = @table
AND COLUMN_NAME = @column";

                AddParameter(cmd, "@table", column.Table);
                AddParameter(cmd, "@column", column.Name);
            }

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result) > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Column existence check failed for {Table}.{Column}", column.Table, column.Name);
            return false;
        }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record CriticalColumn(string Table, string Name);

    private sealed class DatabaseSchemaDriftException : InvalidOperationException
    {
        public DatabaseSchemaDriftException(string message) : base(message) { }
    }
}
