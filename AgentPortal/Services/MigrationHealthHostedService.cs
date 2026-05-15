using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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

    private static readonly string[] CriticalTables =
    {
        "ActionItems", "ActionLogs", "Blockers", "DecisionRecords", "Commitments", "AnalyticsEvents"
    };

    public MigrationHealthHostedService(IServiceProvider services, ILogger<MigrationHealthHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        var provider = db.Database.ProviderName ?? "unknown";
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> TableExistsAsync(
        MasterAppDbContext db,
        string table,
        CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();

            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = @table";

            var param = cmd.CreateParameter();
            param.ParameterName = "@table";
            param.Value = table;
            cmd.Parameters.Add(param);

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
