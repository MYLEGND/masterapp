using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Diagnostics;
using Xunit;
namespace AgentPortal.Tests;
public sealed class RuntimeDiagnosticsPresentationTests
{
    private static string Sample() => JsonSerializer.Serialize(new { sampledUtc = new DateTime(2026,10,30,3,0,0,DateTimeKind.Utc),
        performance = new RuntimeDiagnosticPerformance { ElapsedMs = 13000, Outcome = "failed", Truncated = true,
            InstrumentedBoundaries = ["request", "stages", "database_connection"],
            Stages = [new() { Stage = "authorization", ElapsedMs = 5 }, new() { Stage = "state_read", ElapsedMs = 12000 }],
            Dependencies = [new() { Kind = "database_connection", ElapsedMs = 11000, Count = 2, FailedCount = 1 }] } },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public void ReadableSummaryDistinguishesMeasuredLargestBoundariesMissingAndTruncated()
    {
        var sample = RuntimeDiagnosticPerformanceView.Read(Sample())!;
        Assert.Equal("state_read", sample.LargestStage!.Stage);
        Assert.Equal(12000, sample.LargestStage.ElapsedMs);
        Assert.Equal("database_connection", sample.LargestDependency!.Kind);
        Assert.Contains("http_client", sample.MissingBoundaries);
        Assert.Contains("database_reader_lifetime", sample.MissingBoundaries);
        Assert.True(sample.Evidence.Truncated); Assert.Equal("failed", sample.Evidence.Outcome);
    }
    [Theory] [InlineData(null)] [InlineData("{}")] [InlineData("{\"sampledUtc\":123}")] [InlineData("not json")]
    public void MissingOrMalformedSamplesAreUnknownNotHealthy(string? json) => Assert.Null(RuntimeDiagnosticPerformanceView.Read(json));
    [Fact]
    public async Task ExistingRazorViewRendersActionableSummaryAndPreservesFiltersOnBothPaginationLinks()
    {
        var html = await RenderDiagnosticsAsync();
        Assert.Contains("Largest measured stage", html); Assert.Contains("state read: 12000 ms", html);
        Assert.Contains("13000 ms", html); Assert.Contains("Truncated", html); Assert.Contains("not causal attribution", html);
        var links = Regex.Matches(html, "<a[^>]*href=\"([^\"]+)\"[^>]*>(Previous|Next)</a>");
        Assert.Equal(2, links.Count);
        foreach (Match link in links)
        {
            var uri = new Uri(new Uri("https://fixture.test"), WebUtility.HtmlDecode(link.Groups[1].Value));
            Assert.Equal("/founder/diagnostics", uri.AbsolutePath);
            var query = QueryHelpers.ParseQuery(uri.Query);
            Assert.Equal("ProtectWebsite", query["app"].ToString()); Assert.Equal("Performance", query["category"].ToString());
            Assert.Equal(link.Groups[2].Value == "Previous" ? "1" : "3", query["page"].ToString());
        }
    }

    [Theory]
    [InlineData("Observed")]
    [InlineData("ConfirmedDefect")]
    [InlineData("Dismissed")]
    [InlineData("ManuallyClosed")]
    public async Task DetailReviewRendersSavedDispositionAndCanonicalReturnLink(string disposition)
    {
        var html = await RenderDiagnosticsAsync(details: true, disposition: disposition);
        var select = Regex.Match(html, "<select[^>]*name=\"disposition\"[^>]*>([\\s\\S]*?)</select>");
        Assert.True(select.Success);
        var selected = Regex.Matches(select.Groups[1].Value, "<option[^>]*value=\"([^\"]+)\"[^>]*selected(?:=\"[^\"]*\")?[^>]*>");
        Assert.Single(selected.Cast<Match>());
        Assert.Equal(disposition, selected[0].Groups[1].Value);
        Assert.Contains("name=\"reviewVersion\" value=\"7\"", html);
        Assert.Contains("name=\"confirmDefect\"", html);
        var link = Regex.Match(html, "<a[^>]*href=\"([^\"]+)\"[^>]*>All observations</a>");
        Assert.True(link.Success);
        var uri = new Uri(new Uri("https://fixture.test"), WebUtility.HtmlDecode(link.Groups[1].Value));
        Assert.Equal("/founder/diagnostics", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
    }

    private static async Task<string> RenderDiagnosticsAsync(bool details = false, string disposition = "Observed")
    {
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder.UseEnvironment("Testing").UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting(); services.AddDataProtection();
                services.AddControllersWithViews().AddApplicationPart(typeof(FounderDiagnosticsController).Assembly)
                    .AddApplicationPart(Assembly.Load("AgentPortal.Views"));
            }).Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            }); })).StartAsync();
        using var scope = host.Services.CreateScope(); var services = scope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https"; http.Request.Host = new HostString("fixture.test"); http.Request.Path = "/founder/diagnostics";
        var routes = new RouteData(); routes.Values["controller"] = "FounderDiagnostics"; routes.Values["action"] = "Index";
        http.SetEndpoint(services.GetRequiredService<EndpointDataSource>().Endpoints.First(endpoint =>
            endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(FounderDiagnosticsController) && action.ActionName == "Index"));
        var context = new ActionContext(http, routes, new ActionDescriptor());
        var found = services.GetRequiredService<IRazorViewEngine>().GetView(null, "~/Views/FounderDiagnostics/Index.cshtml", false);
        Assert.True(found.Success);
        var viewData = new ViewDataDictionary<IReadOnlyList<RuntimeDiagnosticIncident>>(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        { Model = new[] { new RuntimeDiagnosticIncident { Id = Guid.NewGuid(), AppIdentifier = "ProtectWebsite", PerformanceEvidenceJson = Sample(), Disposition = disposition, ReviewVersion = 7 } } };
        viewData["Page"] = 2; viewData["HasMore"] = true; viewData["AppFilter"] = "ProtectWebsite"; viewData["CategoryFilter"] = "Performance";
        viewData["Details"] = details;
        using var writer = new StringWriter();
        await found.View.RenderAsync(new ViewContext(context, found.View, viewData,
            new TempDataDictionary(http, services.GetRequiredService<ITempDataProvider>()), writer, new HtmlHelperOptions()));
        return writer.ToString();
    }
}
