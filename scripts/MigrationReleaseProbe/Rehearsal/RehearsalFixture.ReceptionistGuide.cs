using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class RehearsalFixture
{
    // This oracle belongs only to the credential-free disposable SQL rehearsal.
    // InsertData remains unsupported by the production migration observer.
    internal const string SetupGuideMigration = "20261107153025_AddReceptionistSetupGuide";
    private const string SetupGuideTable = "ReceptionistSetupGuideRevisions";
    private const string SetupGuideActor = "system:initial-receptionist-setup-guide";
    private const string BusinessGuideSha256 = "a401ee54e34c139248a46b85f497a63a12591a45787c99ca198ae1ca798c5fb8";
    private const string FounderGuideSha256 = "7c9be5e40534947a2d4aaf55d9a1de64aa30e935505de4c22f425f73fb7a5d90";
    private static readonly Guid SetupGuideRevision = Guid.Parse("3d5b7f2b-2ce8-4c95-8eac-d94ef2b5c6c3");
    private static readonly DateTime SetupGuideTime = new(2026, 11, 7, 15, 0, 0, DateTimeKind.Utc);
    private static string GuideHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static IReadOnlyList<MigrationOperation> ExpectedSetupGuideOperations(string business, string founder)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.CreateTable(SetupGuideTable, table => new {
            Version = table.Column<long>(type: "bigint", nullable: false),
            Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            BusinessGuideJson = table.Column<string>(type: "nvarchar(max)", maxLength: 131072, nullable: false, collation: "Latin1_General_100_BIN2"),
            FounderGuideJson = table.Column<string>(type: "nvarchar(max)", maxLength: 131072, nullable: false, collation: "Latin1_General_100_BIN2"),
            CreatedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
            CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
        }, constraints: table => {
            table.PrimaryKey("PK_ReceptionistSetupGuideRevisions", row => row.Version);
            table.CheckConstraint("CK_ReceptionistSetupGuide_Version", "[Version] >= 1");
        });
        builder.InsertData(SetupGuideTable,
            new[] { "Version", "BusinessGuideJson", "CreatedByActorId", "CreatedUtc", "FounderGuideJson", "Revision" },
            new object[] { 1L, business, SetupGuideActor, SetupGuideTime, founder, SetupGuideRevision });
        builder.CreateIndex("IX_ReceptionistSetupGuideRevisions_Revision", SetupGuideTable, "Revision", unique: true);
        return builder.Operations;
    }

    private static object? GuideOperationValue(object? value)
    {
        if (value is null) return null;
        if (value is MigrationOperation operation) return new {
            Kind = operation.GetType().FullName,
            Properties = operation.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => GuideOperationValue(p.GetValue(operation))),
            Annotations = operation.GetAnnotations().OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => new { a.Name, Value = GuideOperationValue(a.Value) }).ToArray()
        };
        if (value is Type type) return new { Kind = "Type", Value = type.FullName };
        if (value is DateTime date) return new { Kind = "DateTime", date.Ticks, DateKind = date.Kind.ToString() };
        if (value is Array array) return new {
            Kind = array.GetType().FullName,
            Dimensions = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray(),
            Values = array.Cast<object?>().Select(GuideOperationValue).ToArray()
        };
        if (value is IEnumerable sequence && value is not string) return new {
            Kind = value.GetType().FullName, Values = sequence.Cast<object?>().Select(GuideOperationValue).ToArray()
        };
        return new { Kind = value.GetType().FullName, Value = value };
    }

    internal static bool ExactSetupGuideOperations(IReadOnlyList<MigrationOperation> operations)
    {
        if (operations.Count != 3 || operations[1] is not InsertDataOperation seed ||
            seed.Values.GetLength(0) != 1 || seed.Values.GetLength(1) != 6 ||
            seed.Values[0, 1] is not string business || seed.Values[0, 4] is not string founder ||
            GuideHash(business) != BusinessGuideSha256 || GuideHash(founder) != FounderGuideSha256) return false;
        // The expected schema/identity is independent of the candidate. Only the
        // two documents, already matched byte-for-byte by fixed hashes, are reused.
        return operations.Zip(ExpectedSetupGuideOperations(business, founder)).All(pair =>
            JsonSerializer.Serialize(GuideOperationValue(pair.First)) == JsonSerializer.Serialize(GuideOperationValue(pair.Second)));
    }

    private static bool IsSetupGuideFixture(CandidateContract.Entry entry) => entry.Id == SetupGuideMigration &&
        !entry.Supported && JsonSerializer.Serialize(entry, CandidateContract.Json) == JsonSerializer.Serialize(
            CandidateContract.Describe(SetupGuideMigration, ExpectedSetupGuideOperations("", "")), CandidateContract.Json);

    private static bool HasSetupGuideMigration(MasterAppDbContext db) => db.GetService<IMigrationsAssembly>().Migrations.ContainsKey(SetupGuideMigration);

    private static void ValidateSetupGuideFixture(MasterAppDbContext db, CandidateContract.Contract contract)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var entries = contract.Migrations.Where(m => m.Id == SetupGuideMigration).ToArray();
        if (!assembly.Migrations.TryGetValue(SetupGuideMigration, out var migration))
        {
            if (entries.Length != 0) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            return;
        }
        if (entries.Length != 1 || !IsSetupGuideFixture(entries[0]) ||
            !ExactSetupGuideOperations(assembly.CreateMigration(migration, db.Database.ProviderName!).UpOperations))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
    }

    private static async Task VerifySetupGuideSeed(MasterAppDbContext db, bool applied, CancellationToken ct)
    {
        if (!applied) return; // The generic catalog plan proves the entire table absent.
        var seed = await db.Set<ReceptionistSetupGuideRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Version == 1, ct);
        if (seed is null || seed.Revision != SetupGuideRevision || seed.CreatedByActorId != SetupGuideActor ||
            seed.CreatedUtc != SetupGuideTime || GuideHash(seed.BusinessGuideJson) != BusinessGuideSha256 ||
            GuideHash(seed.FounderGuideJson) != FounderGuideSha256)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }

    private static async Task VerifySetupGuideData(MasterAppDbContext db, CancellationToken ct)
    {
        await VerifySetupGuideSeed(db, true, ct);
        if (await db.Set<ReceptionistSetupGuideRevision>().CountAsync(ct) != 1)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        var next = new ReceptionistSetupGuideRevision {
            Version = 2, Revision = Guid.Parse("b2000000-0000-0000-0000-000000000002"),
            CreatedByActorId = "synthetic:guide-publisher", CreatedUtc = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            BusinessGuideJson = "{\"title\":\"Synthetic business revision\",\"introduction\":\"Retained\",\"steps\":[{\"title\":\"One\",\"instructions\":\"Plain text\"}]}",
            FounderGuideJson = "{\"title\":\"Synthetic Founder revision\",\"introduction\":\"Retained\",\"steps\":[{\"title\":\"One\",\"instructions\":\"Plain text\"}]}"
        };
        db.Add(next);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var history = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        await db.Database.MigrateAsync(ct); // No pending migration; must never reseed or overwrite V2.
        var actual = await db.Set<ReceptionistSetupGuideRevision>().AsNoTracking().OrderByDescending(x => x.Version).FirstAsync(ct);
        if (actual.Version != next.Version || actual.Revision != next.Revision || actual.CreatedByActorId != next.CreatedByActorId ||
            actual.CreatedUtc != next.CreatedUtc || actual.BusinessGuideJson != next.BusinessGuideJson || actual.FounderGuideJson != next.FounderGuideJson ||
            await db.Set<ReceptionistSetupGuideRevision>().CountAsync(ct) != 2 ||
            !(await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(history))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await VerifySetupGuideSeed(db, true, ct);
        foreach (var statement in new[] {
            "UPDATE dbo.ReceptionistSetupGuideRevisions SET Version=0 WHERE Version=2",
            "UPDATE dbo.ReceptionistSetupGuideRevisions SET Version=1 WHERE Version=2",
            "UPDATE dbo.ReceptionistSetupGuideRevisions SET Revision='3d5b7f2b-2ce8-4c95-8eac-d94ef2b5c6c3' WHERE Version=2"
        })
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try { await db.Database.ExecuteSqlRawAsync(statement, ct); throw new InvalidOperationException("Invalid guide identity accepted"); }
            catch (SqlException error) when (error.Number is 547 or 2601 or 2627) { }
            finally { await transaction.RollbackAsync(ct); }
        }
    }

    private static IEnumerable<string> SetupGuideDrift() => [
        "ALTER INDEX IX_ReceptionistSetupGuideRevisions_Revision ON dbo.ReceptionistSetupGuideRevisions DISABLE",
        "ALTER TABLE dbo.ReceptionistSetupGuideRevisions NOCHECK CONSTRAINT CK_ReceptionistSetupGuide_Version",
        "ALTER TABLE dbo.ReceptionistSetupGuideRevisions ALTER COLUMN CreatedByActorId nvarchar(449) COLLATE Latin1_General_100_BIN2 NOT NULL",
        "ALTER TABLE dbo.ReceptionistSetupGuideRevisions ALTER COLUMN FounderGuideJson nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL",
        "DELETE FROM dbo.ReceptionistSetupGuideRevisions WHERE Version=1",
        "UPDATE dbo.ReceptionistSetupGuideRevisions SET BusinessGuideJson=N'changed' WHERE Version=1",
        "UPDATE dbo.ReceptionistSetupGuideRevisions SET FounderGuideJson=N'changed' WHERE Version=1",
        "UPDATE dbo.ReceptionistSetupGuideRevisions SET CreatedByActorId=N'changed' WHERE Version=1",
        "UPDATE dbo.ReceptionistSetupGuideRevisions SET CreatedUtc='2026-11-07T15:00:01' WHERE Version=1",
        "UPDATE dbo.ReceptionistSetupGuideRevisions SET Revision='b2000000-0000-0000-0000-000000000099' WHERE Version=1"
    ];
}
