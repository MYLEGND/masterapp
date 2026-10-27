using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class RehearsalFixture
{
    internal static bool IsBrowserFixture(CandidateContract.Entry entry) => entry.Id == CandidateContract.BrowserMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) ==
            "da3529bf9f9a13b41ad6b342a2fc65d293f2e75a1fa282123bd1812bf6a4c4af";
    private static string[] BrowserDrift() => [
        "ALTER TABLE dbo.ReceptionistCalls NOCHECK CONSTRAINT CK_ReceptionistCall_BrowserCreation",
        "ALTER TABLE dbo.ReceptionistCalls NOCHECK CONSTRAINT CK_ReceptionistCall_BrowserCreation; ALTER TABLE dbo.ReceptionistCalls CHECK CONSTRAINT CK_ReceptionistCall_BrowserCreation",
        "DROP INDEX IX_ReceptionistCalls_BrowserCreationId ON dbo.ReceptionistCalls",
        "DROP INDEX IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId ON dbo.ReceptionistCalls; CREATE UNIQUE INDEX IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId ON dbo.ReceptionistCalls(Provider,ProviderProjectKey,ProviderSessionId)",
        "ALTER TABLE dbo.ReceptionistCalls DROP CONSTRAINT CK_ReceptionistCall_BrowserCreation; ALTER TABLE dbo.ReceptionistCalls ALTER COLUMN BrowserOfferSha256 nvarchar(63) COLLATE Latin1_General_100_BIN2 NULL",
        "ALTER TABLE dbo.ReceptionistCalls DROP CONSTRAINT CK_ReceptionistCall_BrowserCreation; DROP INDEX IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId ON dbo.ReceptionistCalls; ALTER TABLE dbo.ReceptionistCalls ALTER COLUMN ProviderSessionId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL; CREATE UNIQUE INDEX IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId ON dbo.ReceptionistCalls(Provider,ProviderProjectKey,ProviderSessionId) WHERE ProviderSessionId IS NOT NULL"
    ];
    private static async Task VerifyBrowserData(MasterAppDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Reuse the two existing, independently scoped SIP/accounting fixtures.
        // Both NULL provider identifiers must coexist without invented sessions.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE dbo.ReceptionistCalls SET BrowserCreationId=NEWID(), BrowserOfferSha256=REPLICATE('a',64),
                ProviderAcceptAttemptId=NEWID(), ProviderAcceptWorkerId='synthetic-browser-worker', ProviderAcceptAttemptedUtc='20260101', ProviderSessionId=NULL, State='uncertain'
            WHERE Id IN ('00000000-0000-0000-0000-000000000501','00000000-0000-0000-0000-000000000502');
            """, ct);
        foreach (var (sql, name) in new[] {
            ("UPDATE dbo.ReceptionistCalls SET BrowserCreationId=(SELECT BrowserCreationId FROM dbo.ReceptionistCalls WHERE Id='00000000-0000-0000-0000-000000000501') WHERE Id='00000000-0000-0000-0000-000000000502'", "IX_ReceptionistCalls_BrowserCreationId"),
            ("UPDATE dbo.ReceptionistCalls SET BrowserOfferSha256=NULL", "CK_ReceptionistCall_BrowserCreation"),
            ("UPDATE dbo.ReceptionistCalls SET BrowserOfferSha256=REPLICATE('a',63)", "CK_ReceptionistCall_BrowserCreation"),
            ("UPDATE dbo.ReceptionistCalls SET ProviderAcceptAttemptId=NULL, ProviderAcceptWorkerId=NULL, ProviderAcceptAttemptedUtc=NULL", "CK_ReceptionistCall_BrowserCreation"),
            ("UPDATE dbo.ReceptionistCalls SET State='reserved'", "CK_ReceptionistCall_BrowserCreation"),
            ("UPDATE dbo.ReceptionistCalls SET BrowserCreationId=NULL, BrowserOfferSha256=NULL", "CK_ReceptionistCall_BrowserCreation")
        })
        {
            await transaction.CreateSavepointAsync("browser_probe", ct);
            var refused = false;
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch (SqlException error) when (error.Number is 547 or 2601 or 2627 && error.Message.Contains(name, StringComparison.Ordinal)) { refused = true; }
            finally { await transaction.RollbackToSavepointAsync("browser_probe", ct); }
            if (!refused) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[CandidateContract.BrowserMigration], db.Database.ProviderName!);
        // Test actual generated Down in the disposable transaction, never a production rollback.
        foreach (var bound in new[] { false, true })
        {
            if (bound) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistCalls SET ProviderSessionId=CONVERT(nvarchar(36),Id)", ct);
            await transaction.CreateSavepointAsync("browser_down", ct);
            var before = await BrowserDownSnapshot(db, ct);
            var refused = false;
            try
            {
                try
                {
                foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations))
                    await db.Database.ExecuteSqlRawAsync(command.CommandText, ct);
            }
                catch (SqlException error) when (error.Number == 51000) { refused = true; }
                if (!refused || before != await BrowserDownSnapshot(db, ct))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                await CandidateContract.Verify(db, CandidateContract.Current(db), db.Database.GetMigrations().ToArray(), ct);
            }
            finally { await transaction.RollbackToSavepointAsync("browser_down", ct); }
        }
        await transaction.CreateSavepointAsync("browser_unique", ct);
        var duplicateRefused = false;
        try { await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistCalls SET ProviderSessionId='same-session'", ct); }
        catch (SqlException error) when (error.Number is 2601 or 2627 && error.Message.Contains("IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId", StringComparison.Ordinal)) { duplicateRefused = true; }
        finally { await transaction.RollbackToSavepointAsync("browser_unique", ct); }
        if (!duplicateRefused) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await transaction.RollbackAsync(ct);
    }
    private static async Task<string> BrowserDownSnapshot(MasterAppDbContext db, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT
              (SELECT * FROM dbo.ReceptionistCalls ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS calls,
              (SELECT * FROM dbo.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH, INCLUDE_NULL_VALUES) AS history
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            """;
        var value = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

}
