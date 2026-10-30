using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

[Collection("CrmHistoryEvidence") ]
public sealed partial class CrmHistoryTests
{
    [Fact]
    public void ServerIssuedDisclosureSurvivesLaterDefinitionEdits()
    {
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(services, protection);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var answers = new System.Collections.Generic.Dictionary<string, object> { ["consent"] = true };
        var token = Infrastructure.Leads.WebsiteSubmissionEvidence.ProtectAuthorization(protection, answers.GetType().FullName!, "Consent", "Original issued disclosure");
        var lead = new WebsiteLead { FirstName = "Synthetic", Email = "synthetic@example.test", LeadId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow };
        Infrastructure.Leads.WebsiteSubmissionEvidence.Attach(lead, answers, "Synthetic", "Consent", true, "Later changed disclosure", null, token, provider);
        using var evidence = System.Text.Json.JsonDocument.Parse(lead.MetadataJson!);
        Assert.Equal("Original issued disclosure", evidence.RootElement.GetProperty("SubmittedFormEvidence").GetProperty("authorizations")[0].GetProperty("text").GetString());
        Assert.DoesNotContain(token, lead.MetadataJson);
        Assert.Throws<InvalidOperationException>(() => Infrastructure.Leads.WebsiteSubmissionEvidence.Attach(lead, answers, "Synthetic", "OtherField", true, "Changed", null, token, provider));
    }

    [Fact]
    public void SubmissionEvidenceFreezesAnswersAndDoesNotInferAuthorization()
    {
        var lead = new WebsiteLead { FirstName = "Synthetic", Email = "synthetic@example.test", LeadId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow, SourcePageKey = "synthetic-form" };
        var answers = new System.Collections.Generic.Dictionary<string, object>
        { ["answer"] = false, ["amount"] = 0, ["choices"] = new[] { "one", "two" }, ["optional"] = "" };
        const string presentation = "{\"schemaVersion\":1,\"fields\":[{\"name\":\"answer\",\"question\":\"Original wording\",\"state\":\"Unchecked\",\"answer\":false}]}";
        Infrastructure.Leads.WebsiteSubmissionEvidence.Attach(lead, answers, "Synthetic quote", "Consent", false, "Original disclosure", presentation);
        var frozen = lead.MetadataJson;
        answers["answer"] = true;
        using var document = System.Text.Json.JsonDocument.Parse(frozen!);
        var evidence = document.RootElement.GetProperty("SubmittedFormEvidence");
        Assert.False(evidence.GetProperty("answers").GetProperty("answer").GetBoolean());
        Assert.Equal(0, evidence.GetProperty("answers").GetProperty("amount").GetInt32());
        Assert.Equal("", evidence.GetProperty("answers").GetProperty("optional").GetString());
        Assert.Equal(2, evidence.GetProperty("answers").GetProperty("choices").GetArrayLength());
        Assert.Equal("Not checked", evidence.GetProperty("authorizations")[0].GetProperty("status").GetString());
        Assert.Equal("Original disclosure", evidence.GetProperty("authorizations")[0].GetProperty("text").GetString());
        Assert.Equal("Original wording", evidence.GetProperty("presentationEvidence").GetProperty("fields")[0].GetProperty("question").GetString());
        Assert.Equal(frozen, lead.MetadataJson);
    }

    [Fact]
    public async Task SameDayEntriesRemainSeparateAndRetriesAreIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new CrmHistoryStore(db);
        var first = await store.StageAsync(New("one"));
        await store.StageAsync(New("two"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var retry = await store.StageAsync(New("one"));
        await db.SaveChangesAsync();
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(2, await db.Set<CrmHistoryEntry>().CountAsync());
    }

    [Fact]
    public async Task FiledHistoryRejectsOverwriteAndDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var entry = await new CrmHistoryStore(db).StageAsync(New("original"));
        await db.SaveChangesAsync();
        entry.ContentJson = "{\"changed\":true}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Remove(await db.Set<CrmHistoryEntry>().SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task FailedTransactionDoesNotCreateCompletedActivity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await new CrmHistoryStore(db).StageAsync(New("rolled-back"));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<CrmHistoryEntry>().ToListAsync());
    }

    [Fact]
    public async Task LegacyMissingIdsAreStableAcrossBackfillAndUnknownTimeIsLabeled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        const string legacy = "{\"activities\":[{\"type\":\"Note\",\"note\":\"Synthetic original\"}]}";
        var store = new CrmHistoryStore(db);
        await store.BackfillMetadataAsync("lead:synthetic", "WorkstationLead", "synthetic", legacy);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await store.BackfillMetadataAsync("lead:synthetic", "WorkstationLead", "synthetic", legacy);
        await db.SaveChangesAsync();
        var entry = await db.Set<CrmHistoryEntry>().SingleAsync();
        Assert.Equal("unknown", entry.TimePrecision);
        Assert.Contains("Synthetic original", entry.ContentJson);
        Assert.Contains("\"originalRecordedTimeAvailable\":false", entry.ContentJson);
    }

    private static CrmHistoryEntry New(string sourceId) => new()
    {
        RelationshipKey = "lead:synthetic", Source = "SyntheticTest", SourceId = sourceId,
        RecordedBy = "synthetic-owner", OccurredDate = "2026-10-30", TimePrecision = "date",
        ContentJson = "{\"answer\":false,\"amount\":0}"
    };

    [Fact]
    public async Task LegacyEditsAppendLinkedRevisionAndPreserveOriginal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new CrmHistoryStore(db);
        const string original = "{\"activities\":[{\"id\":\"synthetic-note\",\"type\":\"Note\",\"note\":\"Original account\"}]}";
        await store.BackfillMetadataAsync("lead:synthetic", "WorkstationLead", "synthetic", original);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var revision = original.Replace("Original account", "Later correction");
        await store.BackfillMetadataAsync("lead:synthetic", "WorkstationLead", "synthetic", revision);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await store.BackfillMetadataAsync("lead:synthetic", "WorkstationLead", "synthetic", revision);
        await db.SaveChangesAsync();
        var rows = await db.Set<CrmHistoryEntry>().ToListAsync();
        Assert.Equal(2, rows.Count);
        var first = Assert.Single(rows.Where(x => x.Source == "LegacyActivity"));
        Assert.Contains("Original account", first.ContentJson);
        var later = Assert.Single(rows.Where(x => x.Source == "LegacyActivityRevision"));
        Assert.Equal(first.Id, later.RelatedEntryId);
        Assert.Contains("Later correction", later.ContentJson);
    }
    [Fact]
    public async Task DirectHumanAndAiRoutesDenyCrossRelationshipAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "synthetic", AgentUserId = "owner" });
        await db.SaveChangesAsync();
        var entry = await new CrmHistoryStore(db).StageAsync(New("protected"));
        await db.SaveChangesAsync();
        foreach (var principal in new[] { ("other", false), ("owner", true) })
        {
            var controller = BuildController(db, principal.Item1, principal.Item2);
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Entry(entry.Id, "lead", "synthetic"));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Export("lead", "synthetic"));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Entries("lead", "synthetic", null, null, null, null, null, null, null, null, null));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Backfill("lead", "synthetic"));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.Ownership());
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.UnresolvedOwnership());
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.CorrectOwner(new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic unauthorized repair", true)));
        }
    }

    [Fact]
    public async Task InclusiveDstDayAndHistoricalImportDoNotShiftFrozenPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId = "synthetic", AgentUserId = "owner" });
        await db.SaveChangesAsync();
        var store = new CrmHistoryStore(db);
        var stamps = new[] { "2026-03-08T04:59:00Z", "2026-03-08T05:00:00Z", "2026-03-09T03:59:00Z", "2026-03-09T04:00:00Z" };
        for (var i = 0; i < stamps.Length; i++)
        {
            var entry = New(i.ToString()); entry.OccurredDate = null;
            entry.OccurredUtc = DateTime.Parse(stamps[i]).ToUniversalTime();
            await store.StageAsync(entry);
        }
        await db.SaveChangesAsync();
        var controller = BuildController(db, "owner");
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Entries("lead", "synthetic", null, null, null, null, null, null, null, null, null,
            range: "custom", fromDate: "2026-03-08", throughDate: "2026-03-08"));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        var cutoff = json.GetProperty("snapshotUtc").GetDateTime();
        var late = New("late-import"); late.RecordedUtc = new DateTime(2020, 1, 1); late.OccurredDate = "2026-03-08";
        await store.StageAsync(late); await db.SaveChangesAsync();
        response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Entries("lead", "synthetic", null, null, null, null, null, null, null, null, null,
            range: "custom", fromDate: "2026-03-08", throughDate: "2026-03-08", snapshotUtc: cutoff));
        Assert.Equal(2, System.Text.Json.JsonSerializer.SerializeToElement(response.Value).GetProperty("count").GetInt32());
    }


    [Fact]
    public async Task AssociationCorrectionPreservesEvidenceAndAuditsBothRelationshipsOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.AddRange(new WorkstationLeadProfile { LeadId = "original", AgentUserId = "owner" },
            new WorkstationLeadProfile { LeadId = "target", AgentUserId = "owner" });
        var website = new WebsiteLead { FirstName = "Synthetic", Email = "synthetic@example.test", LeadId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow };
        db.WebsiteLeads.Add(website); await db.SaveChangesAsync();
        var link = new WebsiteLeadIntakeLink { Id = Guid.NewGuid(), WebsiteLeadRowId = website.Id, WebsiteLeadPublicId = website.LeadId, WorkstationLeadId = "original",
            AgentUserId = "owner", SubmittedUtc = DateTime.UtcNow, SnapshotJson = "{\"answer\":false}" };
        db.WebsiteLeadIntakeLinks.Add(link);
        await new CrmHistoryStore(db).StageSubmissionAsync(link); await db.SaveChangesAsync();
        var originalTime = link.SubmittedUtc;
        var request = new AgentPortal.Controllers.CrmHistoryController.AssociationCorrection(Guid.NewGuid(), link.Id, "target", "Verified synthetic correction");
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await BuildController(db, "other").CorrectAssociation(request));
        var controller = BuildController(db, "owner");
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.CorrectAssociation(request));
        db.ChangeTracker.Clear();
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.CorrectAssociation(request));
        var preserved = await db.WebsiteLeadIntakeLinks.SingleAsync();
        Assert.Equal("target", preserved.WorkstationLeadId);
        Assert.Equal("{\"answer\":false}", preserved.SnapshotJson);
        Assert.Equal(originalTime, preserved.SubmittedUtc);
        Assert.Equal(2, await db.Set<CrmHistoryEntry>().CountAsync(x => x.Source == "AssociationCorrection"));
        var old = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Entry(CrmHistoryStore.EntryId("lead:original", "WebsiteSubmission", link.Id.ToString("N")), "lead", "original"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, System.Text.Json.JsonSerializer.SerializeToElement(old.Value).GetProperty("submission").ValueKind);
        var native = new ClientProfile { ClientUserId = "native-client", FirstName = "Synthetic" };
        db.ClientProfiles.Add(native); db.AgentClients.Add(new AgentClient { AgentUserId = "owner", ClientUserId = native.ClientUserId });
        await db.SaveChangesAsync();
        var nativeRequest = new AgentPortal.Controllers.CrmHistoryController.AssociationCorrection(Guid.NewGuid(), link.Id, "", "Verified native client", native.Id, preserved.AssociationVersion);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await BuildController(db, "other").CorrectAssociation(nativeRequest));
        // HttpContextAccessor is ambient; restore the authenticated owner after the denial fixture.
        controller = BuildController(db, "owner");
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.CorrectAssociation(nativeRequest));
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.CorrectAssociation(nativeRequest));
        db.ChangeTracker.Clear();
        preserved = await db.WebsiteLeadIntakeLinks.SingleAsync();
        Assert.Null(preserved.WorkstationLeadId); Assert.Equal(native.Id, preserved.HistoryClientProfileId);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await controller.CorrectAssociation(new(Guid.NewGuid(),link.Id,"target","Stale correction",null,nativeRequest.ExpectedVersion)));
        await using (var competing = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options))
        {
            var stale = await competing.WebsiteLeadIntakeLinks.SingleAsync();
            preserved.AssociationVersion = Guid.NewGuid(); await db.SaveChangesAsync();
            stale.AssociationVersion = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => competing.SaveChangesAsync());
        }

        Assert.Equal("{\"answer\":false}", preserved.SnapshotJson); Assert.Equal(originalTime, preserved.SubmittedUtc);
        var detail = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Entry(CrmHistoryStore.EntryId(CrmHistoryStore.ClientKey(native.Id), "WebsiteSubmission", link.Id.ToString("N")), "profile", native.Id.ToString("N")));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, System.Text.Json.JsonSerializer.SerializeToElement(detail.Value).GetProperty("submission").ValueKind);

    }


    [Fact]
    public async Task UnownedOriginalEvidenceCannotBeRewrittenButAnalyticsMayChange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var source = new WebsiteLead { FirstName = "Synthetic", Email = "synthetic@example.test", LeadId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow,
            MetadataJson = "{\"SubmittedFormEvidence\":{\"answer\":false},\"tracking\":1}" };
        db.WebsiteLeads.Add(source); await db.SaveChangesAsync();
        source.MetadataJson = "{\"SubmittedFormEvidence\":{\"answer\":false},\"tracking\":2}";
        await db.SaveChangesAsync();
        source.MetadataJson = "{\"SubmittedFormEvidence\":{\"answer\":true}}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Remove(await db.WebsiteLeads.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NativeSyntheticRestoreRetainsFiledEvidenceAndImmutability()
    {
        await using var source = new SqliteConnection("Data Source=:memory:"); await source.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(source).Options);
        await db.Database.EnsureCreatedAsync();
        var original = await new CrmHistoryStore(db).StageAsync(New("restore")); await db.SaveChangesAsync();
        await using var destination = new SqliteConnection("Data Source=:memory:"); await destination.OpenAsync();
        source.BackupDatabase(destination);
        await using var restored = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(destination).Options);
        var entry = await restored.Set<CrmHistoryEntry>().SingleAsync();
        Assert.Equal(original.Id, entry.Id); Assert.Equal(original.ContentJson, entry.ContentJson);
        Assert.Equal(original.RecordedUtc, entry.RecordedUtc); Assert.Equal(original.FiledUtc, entry.FiledUtc);
        entry.ContentJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.SaveChangesAsync());
    }

    [Fact]
    public async Task LegacySelfNotesSurviveOwnerTransferWithoutFormerOwnerAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE AgentLeadSelfNotes (AgentUserId TEXT NOT NULL, LeadId TEXT NOT NULL, NoteDate TEXT NOT NULL, WentWell TEXT, CouldBetter TEXT, CreatedUtc TEXT NOT NULL)");
        var lead = new WorkstationLeadProfile { LeadId = "legacy-transfer", AgentUserId = "old-owner" };
        db.WorkstationLeadProfiles.Add(lead); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO AgentLeadSelfNotes VALUES ('old-owner','legacy-transfer','2020-01-01','Verbatim original','Original improvement','2020-01-01 12:00:00')");
        lead.AgentUserId = "new-owner"; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var entry = await db.Set<CrmHistoryEntry>().SingleAsync(x => x.Source == "LegacySelfNote");
        Assert.Equal("old-owner", entry.RecordedBy); Assert.Contains("Verbatim original", entry.ContentJson);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await BuildController(db,"old-owner").Entry(entry.Id,"lead",lead.LeadId));
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await BuildController(db,"new-owner").Entry(entry.Id,"lead",lead.LeadId));
    }

    [Fact]
    public async Task DateOnlyHistorySortsWithInstantsAndUnknownLast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId="mixed",AgentUserId="owner" });await db.SaveChangesAsync();
        var store = new CrmHistoryStore(db);
        foreach (var row in new[] {new CrmHistoryEntry {SourceId="old",OccurredUtc=new DateTime(2020,1,1,12,0,0,DateTimeKind.Utc),TimePrecision="instant"},
            new CrmHistoryEntry {SourceId="new",OccurredDate="2026-10-30",TimePrecision="date"},new CrmHistoryEntry {SourceId="unknown",TimePrecision="unknown"}})
        {row.RelationshipKey="lead:mixed";row.RecordedBy="owner";await store.StageAsync(row);}
        await db.SaveChangesAsync();
        var result=Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await BuildController(db,"owner").Entries("lead","mixed",null,null,null,null,null,null,null,null,null));
        var rows=System.Text.Json.JsonSerializer.SerializeToElement(result.Value).GetProperty("entries");
        Assert.Equal("2026-10-30",rows[0].GetProperty("OccurredDate").GetString()); Assert.Equal("unknown",rows[2].GetProperty("TimePrecision").GetString());
    }

    private static AgentPortal.Controllers.CrmHistoryController BuildController(MasterAppDbContext db, string owner, bool restricted = false)
    {
        var claims = new System.Collections.Generic.List<System.Security.Claims.Claim>
        { new("oid", owner), new("zoneinfo", "America/New_York") };
        if (restricted) claims.Add(new(System.Security.Claims.ClaimTypes.Role, Infrastructure.Identity.RestrictedAiIdentity.Role));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Synthetic")) };
        var agents = new AgentPortal.Services.EffectiveAgentContext(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http }, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentPortal.Services.EffectiveAgentContext>.Instance);
        return new(db, agents, new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string,string?> { ["CrmHistory:NativeClientAssociationsEnabled"] = "true" }).Build(), new AgentPortal.Services.AgentTimeZoneResolver())
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http } };
    }

}
