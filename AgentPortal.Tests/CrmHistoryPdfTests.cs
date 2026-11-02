using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;
public sealed partial class CrmHistoryTests
{
    [Fact]
    public async Task HumanReadablePdfExportRetainsCompleteAuditAndRejectsOtherOwners()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId="synthetic", AgentUserId="owner", FirstName="Synthetic", LastName="PDF review" });
        var entry = New("pdf-test"); entry.Type="Note";
        entry.ContentJson=JsonSerializer.Serialize(new { account="Synthetic case narrative", outcome="No provider confirmation", nextAction="Review original evidence", missing=(string?)null, empty="", answer=false, amount=0, supportingJson="not JSON", emptyObject=new{}, items=new object[]{"First item",new{nested="Nested item"}}, narrative=string.Join("\n",Enumerable.Range(1,80).Select(i=>$"Narrative line {i}: synthetic factual account, retained without truncation.")) });
        await new CrmHistoryStore(db).StageAsync(entry);
        var audit=New("access-test");audit.Source="HistoryAccess";audit.Type="Access";
        await new CrmHistoryStore(db).StageAsync(audit);
        var malformed=New("historical-text");malformed.ContentJson="Original unstructured historical narrative";await new CrmHistoryStore(db).StageAsync(malformed);
        var omitted=New("omitted");omitted.SubmissionId=Guid.NewGuid();await new CrmHistoryStore(db).StageAsync(omitted);
        var document=New("receipt");document.Source="ProtectedDocument";document.Type="Document";
        document.ContentJson=JsonSerializer.Serialize(new AgentPortal.Controllers.CrmEvidenceController.Receipt(new string('a',64),1234,"application/pdf","synthetic.pdf","Synthetic source","Signature evidence unavailable",null));
        await new CrmHistoryStore(db).StageAsync(document);
        var website=new WebsiteLead {FirstName="Synthetic",Email="synthetic@example.test",LeadId=Guid.NewGuid(),CreatedUtc=DateTime.UtcNow};
        db.WebsiteLeads.Add(website);await db.SaveChangesAsync();
        var submission=new WebsiteLeadIntakeLink {Id=Guid.NewGuid(),WebsiteLeadRowId=website.Id,WebsiteLeadPublicId=website.LeadId,WorkstationLeadId="synthetic",AgentUserId="owner",SubmittedUtc=DateTime.UtcNow,SnapshotJson="{\"SubmittedFormEvidence\":{\"completeness\":\"Browser-reported context; authoritative answers retained separately\",\"answer\":false,\"supportingJson\":\"literal non-JSON answer\"}}"};
        db.WebsiteLeadIntakeLinks.Add(submission);await new CrmHistoryStore(db).StageSubmissionAsync(submission);
        var equal=New("equal-distinct");equal.Type="Note";equal.ContentJson=entry.ContentJson;await new CrmHistoryStore(db).StageAsync(equal);
        var reference=New("second-reference");reference.SubmissionId=submission.Id;reference.ContentJson="Second independent event referencing same submission";await new CrmHistoryStore(db).StageAsync(reference);
        await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await BuildController(db,"other").Export("lead","synthetic"));
        Assert.IsType<NotFoundResult>(await BuildController(db,"owner",true).Export("lead","synthetic"));
        var file=Assert.IsType<FileContentResult>(await BuildController(db,"owner").Export("lead","synthetic"));
        Assert.Equal("application/pdf",file.ContentType);Assert.EndsWith(".pdf",file.FileDownloadName);
        Assert.StartsWith("%PDF-",System.Text.Encoding.ASCII.GetString(file.FileContents.Take(8).ToArray()));
        var receipt=await db.Set<CrmHistoryEntry>().SingleAsync(x=>x.Source=="HistoryExport");
        Assert.Contains("\"count\":8",receipt.ContentJson);
        Assert.Contains("\"submissionCount\":1",receipt.ContentJson);
        var output=Environment.GetEnvironmentVariable("LEGEND_CRM_PDF_TEST_OUTPUT");
        if(!string.IsNullOrEmpty(output)) await System.IO.File.WriteAllBytesAsync(output,file.FileContents);
    }

    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("malformed retained text")]
    public void PdfPreservesMissingEmptyAndMalformedSnapshots(string? snapshot)
    {
        QuestPDF.Settings.License=QuestPDF.Infrastructure.LicenseType.Community;
        var report=JsonSerializer.SerializeToElement(new{exportId="synthetic",generatedUtc="2026-11-01T00:00:00Z",relationship="synthetic",entries=Array.Empty<object>(),evidenceIndex=new[]{new{Id="snapshot",SnapshotJson=snapshot}},omittedSubmissions=Array.Empty<object>(),protectedDocuments=Array.Empty<object>(),detailLinks=Array.Empty<object>(),limitations="Synthetic fixture"});
        var render=typeof(AgentPortal.Controllers.CrmHistoryController).GetMethod("RenderCaseHistoryPdf",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var bytes=Assert.IsType<byte[]>(render.Invoke(null,new object?[]{report,"Synthetic","Lead","UTC",null}));
        Assert.StartsWith("%PDF-",System.Text.Encoding.ASCII.GetString(bytes.Take(8).ToArray()));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("evidenceIndex")]
    public void PdfRejectsDuplicateIdentityInsteadOfSilentlyMerging(string duplicateSection)
    {
        var duplicate = new[]{new{Id="same"},new{Id="same"}};
        var report=JsonSerializer.SerializeToElement(new System.Collections.Generic.Dictionary<string,object>{
            ["entries"]=duplicateSection=="entries"?duplicate:Array.Empty<object>(),
            ["evidenceIndex"]=duplicateSection=="evidenceIndex"?duplicate:Array.Empty<object>()});
        var render=typeof(AgentPortal.Controllers.CrmHistoryController).GetMethod("RenderCaseHistoryPdf",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var exception=Assert.Throws<System.Reflection.TargetInvocationException>(()=>render.Invoke(null,new object?[]{report,"Synthetic","Lead","UTC",null}));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Duplicate record identity",exception.InnerException!.Message);
    }

    [Theory]
    [InlineData(10)] [InlineData(25)] [InlineData(50)]
    public async Task CompactPagesPreserveAllFrozenMixedDateRecords(int size)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId="synthetic", AgentUserId="owner" });
        var store=new CrmHistoryStore(db);
        for(var i=0;i<57;i++) { var e=New("page-"+i);if(i%3==0){e.OccurredUtc=new DateTime(2026,10,30,12,0,0,DateTimeKind.Utc);e.OccurredDate=null;}else if(i%3==1)e.OccurredDate=null;if(i==0)e.Source="HistoryAccess";if(i==1)e.Source="HistoryExport";await store.StageAsync(e); }
        await db.SaveChangesAsync();var snapshot=DateTime.UtcNow;var ids=new System.Collections.Generic.List<string>();
        var controller=BuildController(db,"owner");
        for(var page=0;page*size<57;page++)
        {
            var result=Assert.IsType<OkObjectResult>(await controller.Entries("lead","synthetic",null,null,null,null,null,null,null,null,null,page:page,snapshotUtc:snapshot,pageSize:size));
            var json=JsonSerializer.SerializeToElement(result.Value);Assert.Equal(size,json.GetProperty("pageSize").GetInt32());Assert.Equal(57,json.GetProperty("count").GetInt32());
            ids.AddRange(json.GetProperty("entries").EnumerateArray().Select(x=>x.GetProperty("Id").GetString()!));
        }
        Assert.Equal(57,ids.Count);Assert.Equal(57,ids.Distinct().Count());
        var filtered=Assert.IsType<OkObjectResult>(await controller.Entries("lead","synthetic",null,null,null,null,null,null,null,null,null,pageSize:size,activityView:"case"));
        Assert.Equal(55,JsonSerializer.SerializeToElement(filtered.Value).GetProperty("count").GetInt32());
        Assert.IsType<BadRequestResult>(await controller.Entries("lead","synthetic",null,null,null,null,null,null,null,null,null,pageSize:11));
        Assert.IsType<BadRequestResult>(await controller.Entries("lead","synthetic",null,null,null,null,null,null,null,null,null,activityView:"hidden"));
    }
}
