using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class RuntimeDiagnosticsPublicWebsiteTests
{
    private const string Origin = "https://www.mylegnd.com";
    private const string Endpoint = "/api/runtime-diagnostics";

    [Fact]
    public async Task PublicWebsite_BootstrapAndPostRequireExactOriginAndBoundAntiforgeryCookie()
    {
        await using var app = await CreateApplication();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://protect.mylegnd.com");
        client.DefaultRequestHeaders.Add("Origin", Origin);
        using var bootstrap = await client.GetAsync(Endpoint + "/bootstrap");
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.Equal(Origin, bootstrap.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", bootstrap.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.True(bootstrap.Headers.CacheControl?.NoStore);
        var text = await bootstrap.Content.ReadAsStringAsync();
        Assert.True(text.Length < 5000);
        using var parsed = JsonDocument.Parse(text);
        Assert.Equal(new[] { "requestToken" }, parsed.RootElement.EnumerateObject().Select(value => value.Name));
        var token = parsed.RootElement.GetProperty("requestToken").GetString();
        var cookie = bootstrap.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);

        var observation = new RuntimeDiagnosticEvent { AppIdentifier = "Legend-Website", ErrorName = "TypeError", Route = "/about" };
        using var noToken = await client.PostAsJsonAsync(Endpoint, observation);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        using var noCookie = await client.PostAsJsonAsync(Endpoint, observation);
        Assert.Equal(HttpStatusCode.BadRequest, noCookie.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var accepted = await client.PostAsJsonAsync(Endpoint, observation);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(string.Empty, await accepted.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://false-website.example");
        using var rejected = await client.PostAsJsonAsync(Endpoint, observation);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Access-Control-Allow-Origin"));
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().RuntimeDiagnosticIncidents.ToListAsync());
        using var noRead = await client.GetAsync(Endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, noRead.StatusCode);
    }

    [Theory]
    [InlineData("https://false-website.example")]
    [InlineData("https://www.mylegnd.com.false-website.example")]
    [InlineData("http://www.mylegnd.com")]
    [InlineData("null")]
    public async Task BootstrapRejectsUnconfiguredOriginsWithoutCookieOrToken(string origin)
    {
        await using var app = await CreateApplication();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://protect.mylegnd.com");
        client.DefaultRequestHeaders.Add("Origin", origin);
        using var response = await client.GetAsync(Endpoint + "/bootstrap");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("requestToken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PreflightCredentialsAreLimitedToDiagnosticsAndDeclaredHeaders()
    {
        await using var app = await CreateApplication();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://protect.mylegnd.com");
        foreach (var path in new[] { Endpoint, "/api/website-content/public/legend" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", Origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,requestverificationtoken");
            using var response = await client.SendAsync(request);
            Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal(path == Endpoint, response.Headers.Contains("Access-Control-Allow-Credentials"));
            if (path == Endpoint)
            {
                var headers = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
                Assert.Contains("RequestVerificationToken", headers, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain('*', headers);
                Assert.DoesNotContain("Authorization", headers, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.mylegnd.com")]
    [InlineData("http://www.mylegnd.com")]
    [InlineData("https://www.mylegnd.com/path")]
    public void PublicTransportConfigurationRefusesWildcardsAndNonOrigins(string origin)
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddRuntimeDiagnosticPublicWebsiteTransport(new[] { origin }));
    }

    private static async Task<WebApplication> CreateApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var databaseName = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<MasterAppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddRuntimeDiagnostics(builder.Configuration, builder.Environment);
        var origins = new[] { Origin, "https://mylegnd.com", "https://protect.mylegnd.com" };
        builder.Services.AddRuntimeDiagnosticPublicWebsiteTransport(origins);
        builder.Services.AddCors(options => options.AddPolicy("PublicWebsiteEditor", policy => policy
            .WithOrigins(origins).AllowAnyHeader().WithMethods("GET", "POST")));
        var app = builder.Build();
        app.UseRouting();
        app.UseWhen(context => context.Request.Path.StartsWithSegments(Endpoint),
            branch => branch.UseCors(RuntimeDiagnosticsExtensions.PublicWebsiteCorsPolicy));
        app.UseWhen(context => !context.Request.Path.StartsWithSegments(Endpoint),
            branch => branch.UseCors("PublicWebsiteEditor"));
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
