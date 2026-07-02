using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data;

public static class MasterAppSqliteSchemaBootstrapper
{
    private const string LegacyBootstrapBaselineMigrationId = "20260618104500_AddAnalyticsDriftAlerts";
    private const string CommerceBusinessScopeMigrationId = "20260702160000_AddCommerceBusinessScope";
    private const string CommerceCoreSchemaMigrationId = "20260702164641_AddCommerceCoreSchema";

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

    private static readonly IndexPatch[] CommerceBusinessIndexes =
    {
        new("IX_CommerceBusinesses_IsActive", "CREATE INDEX \"IX_CommerceBusinesses_IsActive\" ON \"CommerceBusinesses\" (\"IsActive\")"),
        new("IX_CommerceBusinesses_Key", "CREATE UNIQUE INDEX \"IX_CommerceBusinesses_Key\" ON \"CommerceBusinesses\" (\"Key\")")
    };

    private static readonly IndexPatch[] CommerceCoreIndexes =
    {
        new("IX_CommerceBusinessSettings_CommerceBusinessId", "CREATE UNIQUE INDEX \"IX_CommerceBusinessSettings_CommerceBusinessId\" ON \"CommerceBusinessSettings\" (\"CommerceBusinessId\")"),
        new("IX_CommerceOrderLines_CommerceOrderId", "CREATE INDEX \"IX_CommerceOrderLines_CommerceOrderId\" ON \"CommerceOrderLines\" (\"CommerceOrderId\")"),
        new("IX_CommerceOrders_CheckoutAttemptId", "CREATE INDEX \"IX_CommerceOrders_CheckoutAttemptId\" ON \"CommerceOrders\" (\"CheckoutAttemptId\")"),
        new("IX_CommerceOrders_CommerceBusinessId_CreatedUtc", "CREATE INDEX \"IX_CommerceOrders_CommerceBusinessId_CreatedUtc\" ON \"CommerceOrders\" (\"CommerceBusinessId\", \"CreatedUtc\")"),
        new("IX_CommerceOrders_CommerceBusinessId_OrderNumber", "CREATE UNIQUE INDEX \"IX_CommerceOrders_CommerceBusinessId_OrderNumber\" ON \"CommerceOrders\" (\"CommerceBusinessId\", \"OrderNumber\")"),
        new("IX_CommerceOrders_CommerceBusinessId_PaymentStatus_FulfillmentStatus", "CREATE INDEX \"IX_CommerceOrders_CommerceBusinessId_PaymentStatus_FulfillmentStatus\" ON \"CommerceOrders\" (\"CommerceBusinessId\", \"PaymentStatus\", \"FulfillmentStatus\")"),
        new("IX_CommerceProductDiscounts_CommerceProductId_Code", "CREATE INDEX \"IX_CommerceProductDiscounts_CommerceProductId_Code\" ON \"CommerceProductDiscounts\" (\"CommerceProductId\", \"Code\")"),
        new("IX_CommerceProductDiscounts_CommerceProductId_ExternalDiscountKey", "CREATE UNIQUE INDEX \"IX_CommerceProductDiscounts_CommerceProductId_ExternalDiscountKey\" ON \"CommerceProductDiscounts\" (\"CommerceProductId\", \"ExternalDiscountKey\")"),
        new("IX_CommerceProductImages_CommerceProductId_DisplayOrder", "CREATE INDEX \"IX_CommerceProductImages_CommerceProductId_DisplayOrder\" ON \"CommerceProductImages\" (\"CommerceProductId\", \"DisplayOrder\")"),
        new("IX_CommerceProductImages_CommerceProductId_ExternalImageKey", "CREATE UNIQUE INDEX \"IX_CommerceProductImages_CommerceProductId_ExternalImageKey\" ON \"CommerceProductImages\" (\"CommerceProductId\", \"ExternalImageKey\")"),
        new("IX_CommerceProductInventoryItems_CommerceProductId_Size", "CREATE UNIQUE INDEX \"IX_CommerceProductInventoryItems_CommerceProductId_Size\" ON \"CommerceProductInventoryItems\" (\"CommerceProductId\", \"Size\")"),
        new("IX_CommerceProducts_CommerceBusinessId_ExternalProductKey", "CREATE UNIQUE INDEX \"IX_CommerceProducts_CommerceBusinessId_ExternalProductKey\" ON \"CommerceProducts\" (\"CommerceBusinessId\", \"ExternalProductKey\")"),
        new("IX_CommerceProducts_CommerceBusinessId_IsActive_DisplayOrder", "CREATE INDEX \"IX_CommerceProducts_CommerceBusinessId_IsActive_DisplayOrder\" ON \"CommerceProducts\" (\"CommerceBusinessId\", \"IsActive\", \"DisplayOrder\")"),
        new("IX_CommerceProducts_CommerceBusinessId_Slug", "CREATE UNIQUE INDEX \"IX_CommerceProducts_CommerceBusinessId_Slug\" ON \"CommerceProducts\" (\"CommerceBusinessId\", \"Slug\")")
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
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken);

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

            if (await CreateCommerceBusinessesTableIfMissingAsync(connection, cancellationToken))
            {
                repairs.Add("CommerceBusinesses");
            }

            foreach (var index in CommerceBusinessIndexes)
            {
                if (await CreateIndexIfMissingAsync(connection, index, cancellationToken))
                {
                    repairs.Add(index.Name);
                }
            }

            foreach (var table in await CreateCommerceCoreTablesIfMissingAsync(connection, cancellationToken))
            {
                repairs.Add(table);
            }

            foreach (var index in CommerceCoreIndexes)
            {
                if (await CreateIndexIfMissingAsync(connection, index, cancellationToken))
                {
                    repairs.Add(index.Name);
                }
            }

            await StampMigrationHistoryAsync(db, connection, createdFromModel, logger, cancellationToken);
            await StampMigrationIfMissingAsync(db, connection, CommerceBusinessScopeMigrationId, cancellationToken);
            await StampMigrationIfMissingAsync(db, connection, CommerceCoreSchemaMigrationId, cancellationToken);

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


    private static async Task<bool> CreateCommerceBusinessesTableIfMissingAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "CommerceBusinesses", cancellationToken))
        {
            return false;
        }

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE "CommerceBusinesses" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceBusinesses" PRIMARY KEY,
                "Key" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "LegalName" TEXT NOT NULL,
                "BusinessType" TEXT NOT NULL,
                "OwnerEmail" TEXT NOT NULL,
                "PrimaryDomain" TEXT NULL,
                "Status" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            )
            """, cancellationToken);

        return true;
    }

    private static async Task<IReadOnlyList<string>> CreateCommerceCoreTablesIfMissingAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var created = new List<string>();

        async Task CreateAsync(string tableName, string sql)
        {
            if (await TableExistsAsync(connection, tableName, cancellationToken))
                return;

            await ExecuteNonQueryAsync(connection, sql, cancellationToken);
            created.Add(tableName);
        }

        await CreateAsync("CommerceBusinessSettings", """
            CREATE TABLE "CommerceBusinessSettings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceBusinessSettings" PRIMARY KEY,
                "CommerceBusinessId" TEXT NOT NULL,
                "ShippingFeeCents" INTEGER NOT NULL,
                "TaxPercent" decimal(9,4) NOT NULL,
                "GlobalDiscountCode" TEXT NOT NULL,
                "GlobalDiscountType" TEXT NOT NULL,
                "GlobalDiscountAmount" decimal(9,4) NOT NULL,
                "GlobalDiscountIsActive" INTEGER NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_CommerceBusinessSettings_CommerceBusinesses_CommerceBusinessId"
                    FOREIGN KEY ("CommerceBusinessId") REFERENCES "CommerceBusinesses" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceOrders", """
            CREATE TABLE "CommerceOrders" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceOrders" PRIMARY KEY,
                "CommerceBusinessId" TEXT NOT NULL,
                "OrderNumber" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NULL,
                "PaidUtc" TEXT NULL,
                "ShippedUtc" TEXT NULL,
                "FulfilledUtc" TEXT NULL,
                "Status" TEXT NOT NULL,
                "PaymentStatus" TEXT NOT NULL,
                "FulfillmentStatus" TEXT NOT NULL,
                "ReturnStatus" TEXT NOT NULL,
                "CheckoutAttemptId" TEXT NULL,
                "IsPaymentProcessing" INTEGER NOT NULL,
                "PaymentProcessingStartedUtc" TEXT NULL,
                "SquarePaymentId" TEXT NULL,
                "SquareError" TEXT NULL,
                "TrackingCarrier" TEXT NULL,
                "TrackingNumber" TEXT NULL,
                "AdminNotes" TEXT NULL,
                "FirstName" TEXT NOT NULL,
                "LastName" TEXT NOT NULL,
                "Email" TEXT NOT NULL,
                "Phone" TEXT NOT NULL,
                "AddressLine1" TEXT NOT NULL,
                "AddressLine2" TEXT NULL,
                "City" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "PostalCode" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "UserAgent" TEXT NULL,
                "RequestIp" TEXT NULL,
                "SubtotalCents" INTEGER NOT NULL,
                "DiscountCode" TEXT NULL,
                "DiscountLabel" TEXT NULL,
                "DiscountCents" INTEGER NOT NULL,
                "RefundedCents" INTEGER NOT NULL,
                "ShippingCents" INTEGER NOT NULL,
                "TaxCents" INTEGER NOT NULL,
                "TotalCents" INTEGER NOT NULL,
                CONSTRAINT "FK_CommerceOrders_CommerceBusinesses_CommerceBusinessId"
                    FOREIGN KEY ("CommerceBusinessId") REFERENCES "CommerceBusinesses" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceProducts", """
            CREATE TABLE "CommerceProducts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceProducts" PRIMARY KEY,
                "CommerceBusinessId" TEXT NOT NULL,
                "ExternalProductKey" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Slug" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "PriceLabel" TEXT NOT NULL,
                "Badge" TEXT NOT NULL,
                "PriceCents" INTEGER NOT NULL,
                "CompareAtPriceCents" INTEGER NOT NULL,
                "IsFeatured" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "DisplayOrder" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_CommerceProducts_CommerceBusinesses_CommerceBusinessId"
                    FOREIGN KEY ("CommerceBusinessId") REFERENCES "CommerceBusinesses" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceOrderLines", """
            CREATE TABLE "CommerceOrderLines" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceOrderLines" PRIMARY KEY,
                "CommerceOrderId" TEXT NOT NULL,
                "ProductExternalKey" TEXT NOT NULL,
                "ProductName" TEXT NOT NULL,
                "ProductSlug" TEXT NOT NULL,
                "Size" TEXT NOT NULL,
                "Quantity" INTEGER NOT NULL,
                "UnitPriceCents" INTEGER NOT NULL,
                "CompareAtPriceCents" INTEGER NOT NULL,
                "ImageUrl" TEXT NULL,
                CONSTRAINT "FK_CommerceOrderLines_CommerceOrders_CommerceOrderId"
                    FOREIGN KEY ("CommerceOrderId") REFERENCES "CommerceOrders" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceProductDiscounts", """
            CREATE TABLE "CommerceProductDiscounts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceProductDiscounts" PRIMARY KEY,
                "CommerceProductId" TEXT NOT NULL,
                "ExternalDiscountKey" TEXT NOT NULL,
                "Code" TEXT NOT NULL,
                "DiscountType" TEXT NOT NULL,
                "Amount" decimal(9,4) NOT NULL,
                "IsActive" INTEGER NOT NULL,
                CONSTRAINT "FK_CommerceProductDiscounts_CommerceProducts_CommerceProductId"
                    FOREIGN KEY ("CommerceProductId") REFERENCES "CommerceProducts" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceProductImages", """
            CREATE TABLE "CommerceProductImages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceProductImages" PRIMARY KEY,
                "CommerceProductId" TEXT NOT NULL,
                "ExternalImageKey" TEXT NOT NULL,
                "ImageUrl" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "AltText" TEXT NOT NULL,
                "IsPrimary" INTEGER NOT NULL,
                "DisplayOrder" INTEGER NOT NULL,
                "ObjectFit" TEXT NOT NULL,
                "ObjectPositionX" INTEGER NOT NULL,
                "ObjectPositionY" INTEGER NOT NULL,
                "Zoom" decimal(9,4) NOT NULL,
                CONSTRAINT "FK_CommerceProductImages_CommerceProducts_CommerceProductId"
                    FOREIGN KEY ("CommerceProductId") REFERENCES "CommerceProducts" ("Id") ON DELETE CASCADE
            )
            """);

        await CreateAsync("CommerceProductInventoryItems", """
            CREATE TABLE "CommerceProductInventoryItems" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CommerceProductInventoryItems" PRIMARY KEY,
                "CommerceProductId" TEXT NOT NULL,
                "ExternalInventoryKey" TEXT NOT NULL,
                "Size" TEXT NOT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "StockQuantity" INTEGER NOT NULL,
                "LowStockThreshold" INTEGER NOT NULL,
                "DisplayOrder" INTEGER NOT NULL,
                CONSTRAINT "FK_CommerceProductInventoryItems_CommerceProducts_CommerceProductId"
                    FOREIGN KEY ("CommerceProductId") REFERENCES "CommerceProducts" ("Id") ON DELETE CASCADE
            )
            """);

        return created;
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

    private static async Task StampMigrationIfMissingAsync(
        MasterAppDbContext db,
        DbConnection connection,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = """
            SELECT COUNT(1)
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migrationId
            """;

        var existsParam = existsCommand.CreateParameter();
        existsParam.ParameterName = "$migrationId";
        existsParam.Value = migrationId;
        existsCommand.Parameters.Add(existsParam);

        if (await ExecuteScalarIntAsync(existsCommand, cancellationToken) > 0)
        {
            return;
        }

        var productVersion = db.Model.FindAnnotation("ProductVersion")?.Value?.ToString() ?? "10.0.9";

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ($migrationId, $productVersion)
            """;

        var migrationParam = insertCommand.CreateParameter();
        migrationParam.ParameterName = "$migrationId";
        migrationParam.Value = migrationId;
        insertCommand.Parameters.Add(migrationParam);

        var versionParam = insertCommand.CreateParameter();
        versionParam.ParameterName = "$productVersion";
        versionParam.Value = productVersion;
        insertCommand.Parameters.Add(versionParam);

        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
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
