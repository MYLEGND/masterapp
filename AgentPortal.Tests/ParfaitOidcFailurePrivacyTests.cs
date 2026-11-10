using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class ParfaitOidcFailurePrivacyTests
{
    [Theory]
    [InlineData("Correlation failed. synthetic-secret-value")]
    [InlineData("Unable to unprotect the message.State. synthetic-secret-value")]
    [InlineData("<script>synthetic-secret-value</script>")]
    public void ActualStartupFailureHandler_UsesOpaqueReferenceAndNeverAuthenticates(string failure)
    {
        InspectHandler(async (provider, options) =>
        {
            var http = new DefaultHttpContext { RequestServices = provider };
            var context = new RemoteFailureContext(http,
                new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)), options,
                new InvalidOperationException(failure));
            await options.Events.RemoteFailure(context);
            Assert.True(context.Result?.Handled);
            Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
            var location = http.Response.Headers.Location.ToString();
            Assert.StartsWith("/internal/denied?reference=", location);
            Assert.True(Guid.TryParseExact(location.Split('=')[1], "N", out _));
            Assert.DoesNotContain("synthetic-secret-value", location);
            Assert.False(http.User.Identity?.IsAuthenticated ?? false);
        });
    }

    private static void InspectHandler(Func<IServiceProvider, OpenIdConnectOptions, Task> inspect)
    {
        // Minimal-host Program reads these values before ConfigureServices runs.
        // Supply only synthetic startup values and restore every process setting.
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings__MasterAppDb"] = "Server=127.0.0.1,1;Initial Catalog=synthetic;User Id=synthetic;Password=synthetic",
            ["SQLCONNSTR_MasterAppDb"] = "Server=127.0.0.1,1;Initial Catalog=synthetic;User Id=synthetic;Password=synthetic",
            ["AzureAd__ClientSecret"] = "synthetic-not-a-credential"
        };
        var prior = new Dictionary<string, string?>();
        try
        {
            foreach (var item in values)
            {
                prior[item.Key] = Environment.GetEnvironmentVariable(item.Key);
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            using var factory = new HandlerFactory(inspect);
            var error = Record.Exception(() => factory.CreateClient());
            Assert.True(factory.Inspected, error?.ToString());
            Assert.NotNull(error);
            Assert.Contains(nameof(InspectionComplete), error.ToString());
        }
        finally
        {
            foreach (var item in prior) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    private sealed class InspectionComplete : Exception { }
    private sealed class HandlerFactory(Func<IServiceProvider, OpenIdConnectOptions, Task> inspect)
        : WebApplicationFactory<global::ParfaitApp.Controllers.InternalController>
    {
        public bool Inspected { get; private set; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var root = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MASTERAPP.sln"))) root = root.Parent;
            if (root is null) throw new DirectoryNotFoundException("Repository root not found.");
            builder.UseEnvironment("Testing").UseContentRoot(Path.Combine(root.FullName, "ParfaitApp"));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MasterAppDb"] = "Server=127.0.0.1,1;Initial Catalog=synthetic;User Id=synthetic;Password=synthetic",
                ["AzureAd:TenantId"] = "11111111-2222-4333-8444-555555555555",
                ["AzureAd:ClientId"] = "22222222-3333-4444-8555-666666666666",
                ["AzureAd:ClientSecret"] = "synthetic-not-a-credential"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MasterAppDbContext>();
                services.RemoveAll<DbContextOptions<MasterAppDbContext>>();
                services.AddScoped(_ => ControllerTestHelpers.BuildDb());
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                    .Get(OpenIdConnectDefaults.AuthenticationScheme);
                inspect(scope.ServiceProvider, options).GetAwaiter().GetResult();
                Inspected = true;
                // Exercise the actual startup delegate without starting hosted workers,
                // migrations, the web server, or any external identity provider.
                throw new InspectionComplete();
            });
        }
    }
}
