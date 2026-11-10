using System;
using System.IO;
using AgentPortal.Controllers;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class PortalBusinessProvisioningCompositionTests
{
    [Fact]
    public void RealPortalStartupRegistersScopedBusinessProvisioning()
    {
        var priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var priorOwner = Environment.GetEnvironmentVariable("OWNER_EMAIL");
        try
        {
            // Actual Program startup resolves these process-wide identities. Keep
            // synthetic startup from importing local user secrets into later tests.
            Environment.SetEnvironmentVariable("FOUNDER_OID", "11111111-2222-4333-8444-555555555555");
            Environment.SetEnvironmentVariable("OWNER_EMAIL", "synthetic@example.test");
            using var factory = new CompositionFactory();
            var error = Record.Exception(() => factory.CreateClient());
            Assert.True(factory.Inspected, error?.ToString());
            Assert.NotNull(error);
            Assert.Contains(nameof(CompositionInspectedException), error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", priorFounder);
            Environment.SetEnvironmentVariable("OWNER_EMAIL", priorOwner);
        }
    }

    private sealed class CompositionInspectedException : Exception { }

    private sealed class CompositionFactory : WebApplicationFactory<ClientsController>
    {
        public bool Inspected { get; private set; }

        private static string RepositoryRoot()
        {
            foreach (var start in new[] { Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"), Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (string.IsNullOrWhiteSpace(start)) continue;
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                    if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln"))) return directory.FullName;
            }
            throw new DirectoryNotFoundException("Repository root was not found.");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(Path.Combine(RepositoryRoot(), "AgentPortal"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["ConnectionStrings:MasterAppDb"] = "Server=127.0.0.1,1;Database=LegendRehearsal_Composition;User Id=synthetic;Password=synthetic;Connect Timeout=1",
                    ["AzureAd:TenantId"] = "11111111-2222-4333-8444-555555555555",
                    ["AzureAd:ClientId"] = "22222222-3333-4444-8555-666666666666",
                    ["AzureAd:ClientSecret"] = "synthetic-not-a-credential"
                }));
            builder.ConfigureServices(services =>
            {
                // Inspect actual Program registrations; replace only the datastore.
                // Manually registering the service here would conceal the production defect.
                services.RemoveAll<MasterAppDbContext>();
                services.RemoveAll<DbContextOptions<MasterAppDbContext>>();
                services.AddScoped(_ => new MasterAppDbContext(
                    new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase("portal-composition").Options));
                using var provider = services.BuildServiceProvider();
                using var first = provider.CreateScope();
                using var second = provider.CreateScope();
                var service = first.ServiceProvider.GetRequiredService<ICommerceBusinessProvisioningService>();
                Assert.IsType<CommerceBusinessProvisioningService>(service);
                Assert.Same(service, first.ServiceProvider.GetRequiredService<ICommerceBusinessProvisioningService>());
                Assert.NotSame(service, second.ServiceProvider.GetRequiredService<ICommerceBusinessProvisioningService>());
                Inspected = true;
                // Stop before workers, database health checks, or external transports start.
                throw new CompositionInspectedException();
            });
        }
    }
}
