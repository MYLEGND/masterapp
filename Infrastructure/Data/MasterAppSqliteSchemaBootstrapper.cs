using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data;

public static class MasterAppSqliteSchemaBootstrapper
{
    private const string LegacyBootstrapBaselineMigrationId = "20260618104500_AddAnalyticsDriftAlerts";

    private static readonly ColumnPatch[] AdditiveColumnPatches =
    {
        new("WebsiteLeads", "ClientIpAddress", "TEXT"),
        new("WebsiteLeads", "ClientUserAgent", "TEXT"),
        new("WebsiteLeads", "Fbp", "TEXT"),
        new("WebsiteLeads", "Fbc", "TEXT"),
        new("WebsiteLeadIntakeLinks", "Fbp", "TEXT"),
        new("WebsiteLeadIntakeLinks", "Fbc", "TEXT"),
        new("WebsiteLeadIntakeLinks", "ClientIpAddress", "TEXT"),
        new("WebsiteLeadIntakeLinks", "ClientUserAgent", "TEXT"),
        new("MetaSignalEvents", "DeviceType", "TEXT"),
        new("MetaSignalEvents", "Browser", "TEXT"),
        new("MetaSignalEvents", "OperatingSystem", "TEXT"),
        new("MetaSignalEvents", "UserAgent", "TEXT"),
        new("MetaSignalEvents", "ViewportWidth", "INTEGER"),
        new("MetaSignalEvents", "ViewportHeight", "INTEGER"),
        new("MetaSignalEvents", "ScreenWidth", "INTEGER"),
        new("MetaSignalEvents", "ScreenHeight", "INTEGER"),
        new("MetaSignalEvents", "WebDriver", "INTEGER"),
        new("MetaSignalEvents", "IsHeadless", "INTEGER"),
        new("MetaSignalEvents", "MouseMoveCount", "INTEGER"),
        new("MetaSignalEvents", "HumanInteractionCount", "INTEGER"),
        new("MetaSignalEvents", "VisibilityChangeCount", "INTEGER"),
        new("MetaSignalEvents", "Language", "TEXT"),
        new("MetaSignalEvents", "TimeZone", "TEXT")
    };

    private static readonly IndexPatch[] AnalyticsDriftAlertIndexes =
    {
        new("IX_AnalyticsDriftAlerts_EventType", "CREATE INDEX \"IX_AnalyticsDriftAlerts_EventType\" ON \"AnalyticsDriftAlerts\" (\"EventType\")"),
        new("IX_AnalyticsDriftAlerts_IncidentKey", "CREATE INDEX \"IX_AnalyticsDriftAlerts_IncidentKey\" ON \"AnalyticsDriftAlerts\" (\"IncidentKey\")"),
        new("IX_AnalyticsDriftAlerts_IsActive", "CREATE INDEX \"IX_AnalyticsDriftAlerts_IsActive\" ON \"AnalyticsDriftAlerts\" (\"IsActive\")"),
        new("IX_AnalyticsDriftAlerts_IsActive_Severity_ObservedUtc", "CREATE INDEX \"IX_AnalyticsDriftAlerts_IsActive_Severity_ObservedUtc\" ON \"AnalyticsDriftAlerts\" (\"IsActive\", \"Severity\", \"ObservedUtc\")"),
        new("IX_AnalyticsDriftAlerts_ObservedUtc", "CREATE INDEX \"IX_AnalyticsDriftAlerts_ObservedUtc\" ON \"AnalyticsDriftAlerts\" (\"ObservedUtc\")"),
        new("IX_AnalyticsDriftAlerts_ScopeKey_ObservedUtc", "CREATE INDEX \"IX_AnalyticsDriftAlerts_ScopeKey_ObservedUtc\" ON \"AnalyticsDriftAlerts\" (\"ScopeKey\", \"ObservedUtc\")"),
        new("IX_AnalyticsDriftAlerts_Severity", "CREATE INDEX \"IX_AnalyticsDriftAlerts_Severity\" ON \"AnalyticsDriftAlerts\" (\"Severity\")")
    };

    public static async Task InitializeAsync(
        MasterAppDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        var createdFromModel = await db.Database.EnsureCreatedAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            var repairs = new List<string>();

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """, cancellationToken);

            foreach (var patch in AdditiveColumnPatches)
            {
                if (await AddColumnIfMissingAsync(connection, patch, cancellationToken))
                {
                    repairs.Add($"{patch.Table}.{patch.Column}");
                }
            }

            if (await CreateAnalyticsDriftAlertsTableIfMissingAsync(connection, cancellationToken))
            {
                repairs.Add("AnalyticsDriftAlerts");
            }

            foreach (var index in AnalyticsDriftAlertIndexes)
            {
                if (await CreateIndexIfMissingAsync(connection, index, cancellationToken))
                {
                    repairs.Add(index.Name);
                }
            }

            await StampMigrationHistoryAsync(db, connection, createdFromModel, logger, cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);

            if (createdFromModel)
            {
                logger.LogInformation("SQLite database was created from the current model and stamped with migration history.");
            }

            if (repairs.Count > 0)
            {
                logger.LogWarning(
                    "SQLite schema drift repaired additively. Applied patches: {Patches}",
                    string.Join(", ", repairs));
            }
            else
            {
                logger.LogInformation("SQLite schema already satisfied the current additive baseline.");
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> AddColumnIfMissingAsync(
        DbConnection connection,
        ColumnPatch patch,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, patch.Table, patch.Column, cancellationToken))
        {
            return false;
        }

        await ExecuteNonQueryAsync(
            connection,
            $"ALTER TABLE \"{patch.Table}\" ADD COLUMN \"{patch.Column}\" {patch.SqlType}",
            cancellationToken);

        return true;
    }

    private static async Task<bool> CreateAnalyticsDriftAlertsTableIfMissingAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "AnalyticsDriftAlerts", cancellationToken))
        {
            return false;
        }

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE "AnalyticsDriftAlerts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AnalyticsDriftAlerts" PRIMARY KEY AUTOINCREMENT,
                "IncidentKey" TEXT NOT NULL,
                "MetricKey" TEXT NOT NULL,
                "EventType" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "MetricUnit" TEXT NOT NULL,
                "CurrentValue" decimal(18,4) NOT NULL,
                "BaselineValue" decimal(18,4) NOT NULL,
                "DeviationPercent" decimal(18,4) NOT NULL,
                "ScopeKey" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "WindowStartUtc" TEXT NOT NULL,
                "WindowEndUtc" TEXT NOT NULL,
                "FirstDetectedUtc" TEXT NOT NULL,
                "LastDetectedUtc" TEXT NOT NULL,
                "ObservedUtc" TEXT NOT NULL,
                "ResolvedUtc" TEXT NULL,
                "LastNotifiedUtc" TEXT NULL,
                "Summary" TEXT NULL,
                "DetailsJson" TEXT NULL
            )
            """, cancellationToken);

        return true;
    }

    private static async Task<bool> CreateIndexIfMissingAsync(
        DbConnection connection,
        IndexPatch patch,
        CancellationToken cancellationToken)
    {
        if (await IndexExistsAsync(connection, patch.Name, cancellationToken))
        {
            return false;
        }

        await ExecuteNonQueryAsync(connection, patch.Sql, cancellationToken);
        return true;
    }

    private static async Task StampMigrationHistoryAsync(
        MasterAppDbContext db,
        DbConnection connection,
        bool createdFromModel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existingHistoryCount = await ExecuteScalarIntAsync(
            connection,
            "SELECT COUNT(1) FROM \"__EFMigrationsHistory\"",
            cancellationToken);

        if (existingHistoryCount > 0)
        {
            return;
        }

        var allMigrations = db.Database.GetMigrations()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var migrationsToStamp = createdFromModel
            ? allMigrations
            : allMigrations
                .Where(x => string.CompareOrdinal(x, LegacyBootstrapBaselineMigrationId) <= 0)
                .ToList();

        var productVersion = db.Model.FindAnnotation("ProductVersion")?.Value?.ToString() ?? "10.0.2";

        foreach (var migrationId in migrationsToStamp)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $productVersion)
                """;

            var migrationParam = command.CreateParameter();
            migrationParam.ParameterName = "$migrationId";
            migrationParam.Value = migrationId;
            command.Parameters.Add(migrationParam);

            var versionParam = command.CreateParameter();
            versionParam.ParameterName = "$productVersion";
            versionParam.Value = productVersion;
            command.Parameters.Add(versionParam);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (createdFromModel)
        {
            logger.LogInformation("Stamped fresh SQLite database with {Count} EF migrations.", migrationsToStamp.Count);
            return;
        }

        logger.LogWarning(
            "Legacy SQLite database had no EF migration history. Stamped {Count} migrations through {BaselineMigrationId}.",
            migrationsToStamp.Count,
            migrationsToStamp.LastOrDefault() ?? LegacyBootstrapBaselineMigrationId);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM sqlite_master
            WHERE type = 'table' AND name = $name
            """;

        var param = command.CreateParameter();
        param.ParameterName = "$name";
        param.Value = tableName;
        command.Parameters.Add(param);

        return await ExecuteScalarIntAsync(command, cancellationToken) > 0;
    }

    private static async Task<bool> IndexExistsAsync(
        DbConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM sqlite_master
            WHERE type = 'index' AND name = $name
            """;

        var param = command.CreateParameter();
        param.ParameterName = "$name";
        param.Value = indexName;
        command.Parameters.Add(param);

        return await ExecuteScalarIntAsync(command, cancellationToken) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var existingColumn = reader["name"]?.ToString();
            if (string.Equals(existingColumn, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteScalarIntAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ExecuteScalarIntAsync(command, cancellationToken);
    }

    private static async Task<int> ExecuteScalarIntAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private sealed record ColumnPatch(string Table, string Column, string SqlType);

    private sealed record IndexPatch(string Name, string Sql);
}
