using System;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Protect_Website.Controllers;
using Xunit;

namespace AgentPortal.Tests;

public class QuoteLegacyLifeRouteTests
{
    [Theory]
    [InlineData(null, true, "", "/Quote/Life")]
    [InlineData("founder", true, "/protect", "/protect/Quote/Life")]
    [InlineData("agent-one", false, "", "/a/agent-one/Quote/Life")]
    [InlineData(" agent/two ", false, "/protect", "/protect/a/agent%2Ftwo/Quote/Life")]
    [InlineData("  ", false, "", "/Quote/Life")]
    public void LegacyLifePreservesOwnerScopePathBaseAndQuery(
        string? slug, bool founder, string pathBase, string expectedPath)
    {
        var http = new DefaultHttpContext();
        http.Request.PathBase = pathBase;
        http.Request.QueryString = new QueryString("?legendEdit=synthetic-ticket&utm_source=synthetic&returnUrl=https%3A%2F%2Fforeign.invalid");
        http.Items["TrackingSlug"] = slug;
        http.Items["IsFounderPath"] = founder;
        var controller = new QuoteController(new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = Assert.IsType<LocalRedirectResult>(controller.Life());
        Assert.Equal(expectedPath + http.Request.QueryString, result.Url);
        Assert.False(result.Permanent);
        Assert.False(result.PreserveMethod);
    }

    [Theory]
    [InlineData("/Quote/Life", "LifeQuote/LifeQuote")]
    [InlineData("/Quote/Life/legacy", "Quote/Life")]
    [InlineData("/Quote/DentalVisionHearing", "Quote/DentalVisionHearing")]
    public async Task RoutingKeepsCanonicalTypedOwnerAndLegacyAlias(string path, string expected)
    {
        using var server = BuildRoutingServer();
        using var client = server.CreateClient();
        Assert.Equal(expected, await client.GetStringAsync(path));
    }

    [Fact]
    public async Task LegacyLifeDoesNotAcceptPost()
    {
        using var server = BuildRoutingServer();
        using var client = server.CreateClient();
        using var response = await client.PostAsync("/Quote/Life/legacy", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static TestServer BuildRoutingServer() => new(new WebHostBuilder()
        .ConfigureServices(services => services.AddControllers()
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new QuoteControllersOnly())))
        .Configure(app =>
        {
            app.UseRouting();
            // Observe selection only: never instantiate controllers, resolve their services,
            // execute filters or touch database, billing, email or external providers.
            app.Use(async (context, next) =>
            {
                var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
                if (action != null)
                    await context.Response.WriteAsync(action.ControllerName + "/" + action.ActionName);
                else
                    await next();
            });
            app.UseEndpoints(endpoints => endpoints.MapControllerRoute(
                "default", "{controller=Home}/{action=Index}/{id?}"));
        }));

    private sealed class QuoteControllersOnly : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(System.Collections.Generic.IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(QuoteController).GetTypeInfo());
            feature.Controllers.Add(typeof(LifeQuoteController).GetTypeInfo());
        }
    }
}
