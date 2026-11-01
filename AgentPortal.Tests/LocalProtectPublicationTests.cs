using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Infrastructure.WebsitePublishing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
namespace AgentPortal.Tests;
public sealed class LocalProtectPublicationTests
{
    [Fact]
    public async Task SqlServerIsRejectedWithoutConnecting()
    {
        await using var db=new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=unused;Integrated Security=true").Options);
        var env=Mock.Of<IWebHostEnvironment>(e=>e.EnvironmentName=="Development");
        using var services=new ServiceCollection().AddSingleton(env).BuildServiceProvider();
        using var protector=new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider());
        var controller=new WebsitePlatformController(db,protector,new ConfigurationBuilder().Build()) { ControllerContext=new() { HttpContext=new DefaultHttpContext { RequestServices=services } } };
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>controller.BootstrapLocalProtectAsync(Path.GetFullPath("unused.db"),new("legend-local-protect/v1","owner",new())));
        Assert.Equal("local_website_database_scope_required",error.Message);
        Assert.Equal(System.Data.ConnectionState.Closed,db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("Production", false, false)]
    [InlineData("Development", true, false)]
    [InlineData("Development", false, true)]
    [InlineData("Development", false, false)]
    [InlineData("Development", false, false, true)]
    [InlineData("Development", false, false, false, true)]
    [InlineData("Development", false, false, false, false, true)]
    public async Task BootstrapFailsClosedWithoutChangingPublication(string environmentName, bool wrongPath, bool existing, bool wrongOwner = false, bool inactive = false, bool invalidDocument = false)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".db");
        try
        {
            await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite("Data Source="+path).Options);
            await db.Database.EnsureCreatedAsync();
            db.AgentTrackingProfiles.Add(new AgentTrackingProfile { AgentUserId="local-owner",AgentUpn="local@example.invalid",Slug="local",Status=inactive?"inactive":"active" });
            if(existing) db.Add(new WebsiteContentState { OwnerKey="local-owner",SiteKey="protect",DraftJson="{}" });
            await db.SaveChangesAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Founder:Upn"]="local@example.invalid",["WebsitePublishing:CompilerRoot"]=Path.Combine(Path.GetTempPath(),"absent-"+Guid.NewGuid()) }).Build();
            var env=Mock.Of<IWebHostEnvironment>(e=>e.EnvironmentName==environmentName);
            using var services=new ServiceCollection().AddSingleton(env).AddSingleton(new WebsitePageCompiler(env,config)).BuildServiceProvider();
            using var protector=new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider());
            var controller=new WebsitePlatformController(db,protector,config) { ControllerContext=new ControllerContext { HttpContext=new DefaultHttpContext { RequestServices=services } } };
            var document=new WebsiteContentDocument { Pages=new() { ["/"]=new WebsitePageDocument {
                Title="Local",Composition=[new WebsiteCompositionNode { Id="local.heading",Type="heading",Tag="h1",Text="Local" }] } } };
            if (invalidDocument)
            {
                document.Pages["/"].Composition.Add(new WebsiteCompositionNode { Id="local.heading",Type="heading",Tag="h1",Text="Duplicate" });
                Assert.IsType<BadRequestObjectResult>(await controller.BootstrapLocalProtectAsync(path,new WebsitePlatformController.LocalProtectCapture("legend-local-protect/v1","local-owner",document)));
                db.ChangeTracker.Clear();
                Assert.Equal(0,await db.Set<WebsiteContentState>().CountAsync());
                Assert.Equal(0,await db.Set<WebsiteContentVersion>().CountAsync());
                return;
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(()=>controller.BootstrapLocalProtectAsync(wrongPath?path+"wrong":path,new WebsitePlatformController.LocalProtectCapture("legend-local-protect/v1",wrongOwner?"other-owner":"local-owner",document)));
            var expected = environmentName != "Development" || wrongPath ? "local_website_database_scope_required"
                : inactive ? "local_website_active_owner_required"
                : wrongOwner ? "local_website_capture_owner_mismatch"
                : existing ? "local_website_already_exists" : "website_compiler_not_configured";
            Assert.Equal(expected,error.Message);
            db.ChangeTracker.Clear();
            Assert.Equal(existing?1:0,await db.Set<WebsiteContentState>().CountAsync());
            Assert.Equal(0,await db.Set<WebsiteContentVersion>().CountAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
