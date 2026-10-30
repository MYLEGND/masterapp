using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    [Fact]
    public async Task HostedSyntheticHistorySupportsDesktopMobileKeyboardAndProtectedEndpoints()
    {
        Assert.Equal("true",Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("MYLEGND/masterapp",Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"));
        Assert.Equal("Linux",Environment.GetEnvironmentVariable("RUNNER_OS"));
        DirectoryInfo? repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"MASTERAPP.sln")))repo=repo.Parent;
        if(repo is null)repo=new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!);
        Assert.True(File.Exists(Path.Combine(repo.FullName,"MASTERAPP.sln")));
        var directory=Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")!,"crm-browser-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var connection="Data Source="+Path.Combine(directory,"synthetic.db");
        Guid tileId;
        await using(var seed=new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Add(new WorkstationLeadProfile { LeadId="synthetic-browser",AgentUserId="synthetic-owner" });
            var source=new WebsiteLead { LeadId=Guid.NewGuid(),FirstName="Synthetic",Email="synthetic@example.test",CreatedUtc=DateTime.UtcNow };
            seed.Add(source);await seed.SaveChangesAsync();
            var answers=Enumerable.Range(1,100).ToDictionary(x=>"Question "+x,x=>(object)(x==100?"FINAL-SYNTHETIC-ANSWER":"Synthetic answer "+x));
            answers["Explicit No"]=false;answers["Amount"]=0;answers["Unanswered"]="";
            var snapshot=JsonSerializer.Serialize(new { SubmittedFormEvidence=new { formType="Synthetic insurance form",completeness="Synthetic complete fixture",answers,
                authorizations=new[]{new{text=string.Join(" ",Enumerable.Repeat("Original synthetic disclosure.",100))+" END-OF-DISCLOSURE",status="Not checked",source="Synthetic original",signatureEvidence="Evidence unavailable"}} } });
            var link=new WebsiteLeadIntakeLink { Id=Guid.NewGuid(),WebsiteLeadRowId=source.Id,WebsiteLeadPublicId=source.LeadId,WorkstationLeadId="synthetic-browser",AgentUserId="synthetic-owner",SubmittedUtc=source.CreatedUtc,CapturedUtc=source.CreatedUtc,SnapshotJson=snapshot,SourcePageKey="synthetic-insurance-form" };
            seed.Add(link);tileId=(await new CrmHistoryStore(seed).StageSubmissionAsync(link)).Id;await seed.SaveChangesAsync();
            var older=new WebsiteLead { LeadId=Guid.NewGuid(),FirstName="Synthetic earlier",Email="earlier@example.test",CreatedUtc=source.CreatedUtc.AddDays(-10) };
            seed.Add(older);await seed.SaveChangesAsync();
            var oldLink=new WebsiteLeadIntakeLink { Id=Guid.NewGuid(),WebsiteLeadRowId=older.Id,WebsiteLeadPublicId=older.LeadId,WorkstationLeadId="synthetic-browser",AgentUserId="synthetic-owner",SubmittedUtc=older.CreatedUtc,CapturedUtc=older.CreatedUtc,SnapshotJson=snapshot,SourcePageKey="synthetic-older-form" };
            seed.Add(oldLink);await new CrmHistoryStore(seed).StageSubmissionAsync(oldLink);await seed.SaveChangesAsync();
        }
        try
        {
            using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseEnvironment("Testing")
                .UseContentRoot(Path.Combine(repo.FullName,"AgentPortal")).UseWebRoot("wwwroot")
                .UseKestrel(options=>options.Listen(IPAddress.Loopback,0))
                .ConfigureServices(services=>
                {
                    services.AddDbContext<MasterAppDbContext>(options=>options.UseSqlite(connection));
                    services.AddHttpContextAccessor();services.AddDataProtection();
                    services.AddSingleton(Mock.Of<IAgentTrackingService>());services.AddScoped<EffectiveAgentContext>();
                    services.AddSingleton<IAgentTimeZoneResolver,AgentTimeZoneResolver>();
                    services.AddAuthentication("SyntheticCrm").AddScheme<AuthenticationSchemeOptions,SyntheticCrmAuth>("SyntheticCrm",_=>{});
                    services.AddAuthorization(options=>options.AddPolicy("FounderOnly",p=>p.RequireClaim("synthetic-founder","true")));
                    services.AddControllersWithViews(options=>options.Filters.Add<SyntheticCrmPartial>())
                        .AddApplicationPart(typeof(CrmHistoryController).Assembly).AddApplicationPart(Assembly.Load("AgentPortal.Views"));
                    services.AddAntiforgery(options=>options.HeaderName="RequestVerificationToken");
                }).Configure(app=>
                {
                    app.Use(async(context,next)=>
                    {
                        if(context.Request.Path=="/__synthetic/signin")
                        {
                            context.Response.Cookies.Append("SyntheticCrmActor","synthetic-owner",new CookieOptions { HttpOnly=true,SameSite=SameSiteMode.Strict });
                            context.Response.Redirect("/CrmHistory?kind=lead&id=synthetic-browser");return;
                        }
                        await next();
                    });
                    app.UseStaticFiles();app.UseRouting();app.UseAuthentication();app.UseAuthorization();
                    app.UseEndpoints(endpoints=>endpoints.MapControllers());
                })).StartAsync();
            var origin=host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.StartsWith("http://127.0.0.1:",origin);
            using(var client=new System.Net.Http.HttpClient())
            {
                Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(origin+"/CrmHistory/Entry/"+tileId+"?kind=lead&id=synthetic-browser")).StatusCode);
                foreach(var actor in new[]{"other-owner","restricted-ai"})
                {
                    client.DefaultRequestHeaders.Remove("X-Synthetic-Actor");client.DefaultRequestHeaders.Add("X-Synthetic-Actor",actor);
                    Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(origin+"/CrmHistory/Entry/"+tileId+"?kind=lead&id=synthetic-browser")).StatusCode);
                    Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(origin+"/CrmHistory/Entries?kind=lead&id=synthetic-browser")).StatusCode);
                }
            }
            var start=new ProcessStartInfo("node") { WorkingDirectory=repo.FullName,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
            start.ArgumentList.Add("tests/analytics/crm-history-browser.cjs");start.ArgumentList.Add(origin);
            using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
            try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(entireProcessTree:true);throw;}
            var output=await stdout;var error=await stderr;
            Assert.True(process.ExitCode==0,output+error);Assert.Contains("CRM_HOSTED_BROWSER:PASS",output);
            await host.StopAsync();
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }

    // Synthetic test host only. Actual authorization and CRM controllers execute;
    // the compiled feature view is rendered as a partial to isolate unrelated shell dependencies.
    public sealed class SyntheticCrmPartial : IAsyncResultFilter
    {
        public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
        {
            if(context.Controller is CrmHistoryController && context.Result is ViewResult view)
                context.Result=new PartialViewResult { ViewName="~/Views/CrmHistory/Index.cshtml",ViewData=view.ViewData,TempData=view.TempData };
            await next();
        }
    }
    public sealed class SyntheticCrmAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor=Request.Headers["X-Synthetic-Actor"].FirstOrDefault()??Request.Cookies["SyntheticCrmActor"];
            if(string.IsNullOrEmpty(actor))return Task.FromResult(AuthenticateResult.NoResult());
            var claims=new System.Collections.Generic.List<Claim>{new("oid",actor),new(ClaimTypes.NameIdentifier,actor),new(ClaimTypes.Name,actor),new("zoneinfo","UTC")};
            if(actor=="restricted-ai")claims.Add(new(ClaimTypes.Role,Infrastructure.Identity.RestrictedAiIdentity.Role));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
}
