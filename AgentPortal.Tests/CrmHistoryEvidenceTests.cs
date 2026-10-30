using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace AgentPortal.Tests;

[CollectionDefinition("CrmHistoryEvidence", DisableParallelization = true)]
public sealed class CrmHistoryEvidenceCollection { }

public sealed partial class CrmHistoryTests
{
    [Fact]
    public async Task ProtectedEvidenceRetryVersionIntegrityAndDirectAccessAreEnforced()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile { LeadId="evidence",AgentUserId="owner" }); await db.SaveChangesAsync();
        var previousRoot=Environment.GetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT");
        var root=Path.Combine(Path.GetTempPath(),"crm-evidence-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT",root);
            var config=new ConfigurationBuilder().Build();var env=new EvidenceEnvironment(root);
            CrmEvidenceController Controller(string owner,bool ai=false)
            {
                var claims=new System.Collections.Generic.List<Claim>{new("oid",owner)};
                if(ai)claims.Add(new(ClaimTypes.Role,Infrastructure.Identity.RestrictedAiIdentity.Role));
                var http=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity(claims,"synthetic"))};
                var agents=new EffectiveAgentContext(new HttpContextAccessor{HttpContext=http},null!,NullLogger<EffectiveAgentContext>.Instance);
                return new(db,agents,config,env){ControllerContext=new ControllerContext{HttpContext=http}};
            }
            IFormFile File(byte[] bytes)=>new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","original.pdf"){Headers=new HeaderDictionary(),ContentType="application/pdf"};
            var bytes=Encoding.ASCII.GetBytes("%PDF-1.7\nSynthetic original evidence\n%%EOF");var request=Guid.NewGuid();var controller=Controller("owner");
            var result=Assert.IsType<OkObjectResult>(await controller.Upload("lead","evidence",request,"Synthetic source","No independent signature",File(bytes)));
            var id=JsonSerializer.SerializeToElement(result.Value).GetProperty("id").GetGuid();
            Assert.IsType<OkObjectResult>(await controller.Upload("lead","evidence",request,"Synthetic source","No independent signature",File(bytes)));
            Assert.IsType<ConflictObjectResult>(await controller.Upload("lead","evidence",request,"Synthetic source","No independent signature",File(Encoding.ASCII.GetBytes("%PDF-1.7\nChanged\n%%EOF"))));
            Assert.IsType<NotFoundResult>(await Controller("other").Download(id,"lead","evidence"));
            Assert.IsType<NotFoundResult>(await Controller("owner",true).Download(id,"lead","evidence"));
            var download=Assert.IsType<FileContentResult>(await controller.Download(id,"lead","evidence"));Assert.Equal(bytes,download.FileContents);
            Assert.Single(await db.Set<CrmHistoryEntry>().Where(x=>x.Source=="ProtectedDocument").ToListAsync());
            Assert.Single(await db.Set<CrmHistoryEntry>().Where(x=>x.Source=="EvidenceAccess").ToListAsync());
            var rollbackRequest=Guid.NewGuid();
            await using(var transaction=await db.Database.BeginTransactionAsync())
            {
                Assert.IsType<OkObjectResult>(await controller.Upload("lead","evidence",rollbackRequest,"Rollback source","Synthetic",File(bytes)));
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.False(await db.Set<CrmHistoryEntry>().AnyAsync(x=>x.Source=="ProtectedDocument" && x.SourceId==rollbackRequest.ToString("N")));
            Assert.IsType<OkObjectResult>(await controller.Upload("lead","evidence",rollbackRequest,"Rollback source","Synthetic",File(bytes)));
            Assert.Equal(1,await db.Set<CrmHistoryEntry>().CountAsync(x=>x.Source=="ProtectedDocument" && x.SourceId==rollbackRequest.ToString("N")));
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var storage=new CrmEvidenceStorage(config,env);
            await storage.PutAsync("lead:evidence",hash,bytes,default);
            Assert.Equal(bytes,await storage.ReadAsync("lead:evidence",hash,default));
            var stored=Assert.Single(Directory.GetFiles(root,"*.pdf",SearchOption.AllDirectories));
            await System.IO.File.WriteAllTextAsync(stored,"corrupted synthetic bytes");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>storage.ReadAsync("lead:evidence",hash,default));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>storage.PutAsync("lead:evidence",hash,bytes,default));
            Assert.Equal("corrupted synthetic bytes",await System.IO.File.ReadAllTextAsync(stored));
        }
        finally {Environment.SetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT",previousRoot);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class EvidenceEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName {get;set;}="Development";
        public string ApplicationName {get;set;}="Synthetic";
        public string ContentRootPath {get;set;}=root;
        public string WebRootPath {get;set;}=root;
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
        public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
    }
}
