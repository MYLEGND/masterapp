using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Data;

/// <summary>Normal local app startup cannot select production or create a database.</summary>
public static class LocalDevelopmentDatabase
{
    public const string DatabaseName = "MasterAppLocalTest";

    public static string ExistingSqliteConnection(string connectionString)
    {
        var connection = new SqliteConnectionStringBuilder(connectionString);
        if (connection.Mode == SqliteOpenMode.Memory || connection.DataSource == ":memory:" ||
            !File.Exists(connection.DataSource))
            throw new InvalidOperationException("Configured SQLite database does not exist. Startup cannot create an empty replacement; use explicit fixture preparation.");
        connection.Mode = SqliteOpenMode.ReadWrite;
        return connection.ToString();
    }

    public static void ValidateTarget(IHostEnvironment environment, string? connectionString,
        bool isolatedFixture = false)
    {
        if (!environment.IsDevelopment() || isolatedFixture) return;
        try
        {
            var connection = new SqlConnectionStringBuilder(connectionString ?? "");
            var server = connection.DataSource;
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
            var host = server.Split(',')[0];
            if (host is not ("127.0.0.1" or "localhost" or "[::1]") ||
                connection.InitialCatalog != DatabaseName || connection.AttachDBFilename.Length != 0)
                throw new ArgumentException();
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "LOCAL DATABASE BLOCKED: use scripts/db.sh local run <app>. " +
                "Normal development requires the persistent loopback MasterAppLocalTest SQL database; " +
                "production, SQLite fallback and alternate databases are not permitted.");
        }
    }

    public static async Task RequireReadyAsync(MasterAppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Database is unavailable. Run explicit database setup; startup never creates a replacement.");
        if (db.Database.IsSqlServer() && (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Database migrations are pending. Apply them through the explicit database workflow before starting the app.");
    }
}
