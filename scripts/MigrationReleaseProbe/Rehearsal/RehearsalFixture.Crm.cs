using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class RehearsalFixture
{
    internal static bool IsCrmFixture(CandidateContract.Entry entry) =>
        entry.Id == CandidateContract.CrmMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) == CandidateContract.CrmEntrySha256;
    private static readonly Guid CrmReceipt = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CrmPublic = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid CrmClient = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly DateTime CrmTime = new(2026, 10, 1, 12, 30, 0);
    private const string CrmSnapshot = "{\"synthetic\":true,\"answer\":false,\"amount\":0,\"disclosure\":\"Original synthetic text\"}";

    private static async Task SeedCrmData(MasterAppDbContext db, CancellationToken ct)
    {
        var source = new WebsiteLead { LeadId = CrmPublic, FirstName = "Synthetic", Email = "synthetic@example.test", CreatedUtc = CrmTime };
        db.Add(source);
        db.Add(new CommerceBusiness { Id = Guid.Parse("20000000-0000-0000-0000-000000000004"), Key = "synthetic-crm-business", DisplayName = "Synthetic CRM business" });
        db.Add(new WorkstationLeadProfile { LeadId = "synthetic-crm-preserved", AgentUserId = "synthetic-owner", CreatedUtc = CrmTime, UpdatedUtc = CrmTime });
        db.Add(new ClientProfile { Id = CrmClient, ClientUserId = "synthetic-crm-client", FirstName = "Synthetic" });
        await db.SaveChangesAsync(ct);
        db.Add(new WebsiteLeadIntakeLink { Id = CrmReceipt, WebsiteLeadRowId = source.Id, WebsiteLeadPublicId = CrmPublic,
            WorkstationLeadId = "synthetic-crm-preserved", AgentUserId = "synthetic-owner", Bucket = "Synthetic",
            SubmittedUtc = CrmTime, CapturedUtc = CrmTime, SnapshotJson = CrmSnapshot });
        await db.SaveChangesAsync(ct);
    }

    private static async Task VerifyCrmData(MasterAppDbContext db, CancellationToken ct)
    {
        var row = await db.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync(x => x.Id == CrmReceipt, ct);
        if (row.WebsiteLeadPublicId != CrmPublic || row.WorkstationLeadId != "synthetic-crm-preserved" ||
            row.HistoryClientProfileId is not null || row.CommerceBusinessId is not null || row.AssociationVersion != Guid.Empty ||
            row.AgentUserId != "synthetic-owner" || row.SubmittedUtc != CrmTime || row.CapturedUtc != CrmTime || row.SnapshotJson != CrmSnapshot ||
            !await db.WebsiteLeads.AnyAsync(x => x.Id == row.WebsiteLeadRowId && x.LeadId == CrmPublic, ct))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        // Disposable SQL only: prove valid native associations and reject both,
        // neither, cross-business native and nonexistent native targets.
        foreach (var statement in new[] {
            "UPDATE dbo.WebsiteLeadIntakeLinks SET HistoryClientProfileId = '20000000-0000-0000-0000-000000000003' WHERE Id = '20000000-0000-0000-0000-000000000001'",
            "UPDATE dbo.WebsiteLeadIntakeLinks SET WorkstationLeadId = NULL, HistoryClientProfileId = NULL WHERE Id = '20000000-0000-0000-0000-000000000001'",
            "UPDATE dbo.WebsiteLeadIntakeLinks SET WorkstationLeadId = NULL, HistoryClientProfileId = '20000000-0000-0000-0000-000000000003', CommerceBusinessId = '20000000-0000-0000-0000-000000000004' WHERE Id = '20000000-0000-0000-0000-000000000001'",
            "UPDATE dbo.WebsiteLeadIntakeLinks SET WorkstationLeadId = NULL, HistoryClientProfileId = '20000000-0000-0000-0000-000000000099' WHERE Id = '20000000-0000-0000-0000-000000000001'" })
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
                throw new InvalidOperationException("Invalid CRM association accepted");
            }
            catch (SqlException error) when (error.Number == 547 && error.Message.Contains(
                statement.Contains("000000000099", StringComparison.Ordinal)
                    ? "FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId"
                    : "CK_WebsiteLeadIntakeLinks_HistoryAssociation", StringComparison.Ordinal)) { }
            finally { await transaction.RollbackAsync(ct); }
        }
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE dbo.WebsiteLeadIntakeLinks SET WorkstationLeadId = NULL, HistoryClientProfileId = '20000000-0000-0000-0000-000000000003' WHERE Id = '20000000-0000-0000-0000-000000000001'", ct);
            var native = await db.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync(x => x.Id == CrmReceipt, ct);
            if (native.WorkstationLeadId is not null || native.HistoryClientProfileId != CrmClient || native.SnapshotJson != CrmSnapshot)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            await transaction.RollbackAsync(ct);
        }
    }

    private static async Task CrmDrift(MasterAppDbContext db, CandidateContract.Contract contract, string[] applied, bool isApplied, CancellationToken ct)
    {
        var mutations = isApplied ? new[] {
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks NOCHECK CONSTRAINT CK_WebsiteLeadIntakeLinks_HistoryAssociation",
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks NOCHECK CONSTRAINT CK_WebsiteLeadIntakeLinks_HistoryAssociation; ALTER TABLE dbo.WebsiteLeadIntakeLinks CHECK CONSTRAINT CK_WebsiteLeadIntakeLinks_HistoryAssociation",
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks NOCHECK CONSTRAINT FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId; ALTER TABLE dbo.WebsiteLeadIntakeLinks CHECK CONSTRAINT FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId",
            "DECLARE @n sysname = (SELECT d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID('dbo.WebsiteLeadIntakeLinks') AND c.name='AssociationVersion'); EXEC('ALTER TABLE dbo.WebsiteLeadIntakeLinks DROP CONSTRAINT '+QUOTENAME(@n)); ALTER TABLE dbo.WebsiteLeadIntakeLinks ADD DEFAULT ('20000000-0000-0000-0000-000000000001') FOR AssociationVersion",
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks DROP CONSTRAINT FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId; DROP INDEX IX_WebsiteLeadIntakeLinks_HistoryClientProfileId ON dbo.WebsiteLeadIntakeLinks; ALTER TABLE dbo.WebsiteLeadIntakeLinks ALTER COLUMN HistoryClientProfileId varchar(36) NULL",

            "ALTER TABLE dbo.WebsiteLeadIntakeLinks NOCHECK CONSTRAINT FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId",
            "DROP INDEX IX_WebsiteLeadIntakeLinks_HistoryClientProfileId ON dbo.WebsiteLeadIntakeLinks",
            "DROP INDEX IX_CrmHistoryEntries_RelationshipKey_Source_SourceId ON dbo.CrmHistoryEntries",
            "ALTER TABLE dbo.CrmHistoryEntries ALTER COLUMN ContentJson nvarchar(max) NULL",
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks ALTER COLUMN WorkstationLeadId nvarchar(64) NOT NULL"
        } : new[] {
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks ADD HistoryClientProfileId uniqueidentifier NULL",
            "CREATE TABLE dbo.CrmHistoryEntries (Id uniqueidentifier NOT NULL PRIMARY KEY)",
            "ALTER TABLE dbo.WebsiteLeadIntakeLinks ALTER COLUMN WorkstationLeadId nvarchar(64) NULL"
        };
        foreach (var mutation in mutations)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(mutation, ct);
            try { await CandidateContract.Verify(db, contract, applied, ct); throw new InvalidOperationException("CRM schema drift accepted"); }
            catch (ProbeObservationFailure error) when (error.Message == "PHYSICAL_SCHEMA_DRIFT") { }
            finally { await transaction.RollbackAsync(ct); }
        }
    }
}
