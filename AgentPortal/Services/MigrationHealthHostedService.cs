using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentPortal.Services;

/// <summary>
/// Lightweight startup diagnostics to surface migration drift across providers.
/// Logs pending migrations and table presence for core execution surfaces.
/// No schema changes are applied here.
/// </summary>
public sealed class MigrationHealthHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MigrationHealthHostedService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    private static readonly string[] CriticalTables =
    {
        "ActionItems", "ActionLogs", "Blockers", "DecisionRecords", "Commitments", "AnalyticsEvents"
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        var provider = db.Database.ProviderName ?? "unknown";
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            _logger.LogInformation("DB provider {Provider}; pending migrations: {PendingCount}", provider, pending.Count());

            if (db.Database.IsSqlite())
            {
                var dataSource = db.Database.GetDbConnection().DataSource;
                _logger.LogInformation("SQLite data source: {DataSource}", dataSource);
            }

            foreach (var table in CriticalTables)
            {
                var exists = await TableExistsAsync(db, table, cancellationToken);
                if (!exists)
                {
                    _logger.LogWarning("Critical table missing: {Table}. Apply migrations for provider {Provider}.", table, provider);
                }
            }
        }
        catch (Exception ex) when (ShouldSuppressStartupFailure(db, ex))
        {
            _logger.LogWarning(
                ex,
                "Migration health check skipped during local development because the configured database is currently unreachable. " +
                "The app will continue starting in non-strict mode.");
        }
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
}
