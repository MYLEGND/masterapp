using System;
using System.IO;
using System.Linq;
using CommerceEngine = Legend.Commerce.CommerceServiceRegistration;
using ParfaitApp.Controllers;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceCoreOwnershipContractTests
{
    [Fact]
    public void PublicCommerceControllers_CompileOnceInSharedAssembly()
    {
        Assert.Equal("LegendCommerce", typeof(StoreController).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(StoreCartController).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(StoreCheckoutController).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(CommerceManagementController).Assembly.GetName().Name);
    }

    [Fact]
    public void ProductOrdersAndAutomations_HaveOnlyOneSharedImplementation()
    {
        Assert.Equal("LegendCommerce", typeof(ParfaitProductService).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(ParfaitOrderService).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(ParfaitCustomerAutomationService).Assembly.GetName().Name);
        Assert.Equal("LegendCommerce", typeof(CommerceEngine).Assembly.GetName().Name);

        foreach (var source in new[]
        {
            "ParfaitProductService.cs", "ParfaitOrderService.cs",
            "ParfaitCustomerAutomationService.cs", "ParfaitStoragePaths.cs"
        })
        {
            Assert.True(File.Exists(Path.Combine(Root(), "CommerceCore", "Services", source)));
            Assert.False(File.Exists(Path.Combine(Root(), "ParfaitApp", "Services", source)));
        }
    }

    [Fact]
    public void LegacyParfaitHost_UsesCanonicalDependencyInjectionAndExternalControllers()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "ParfaitApp", "Program.cs"));
        Assert.Contains("AddLegendCommerceCore()", program, StringComparison.Ordinal);
        Assert.Contains("AddApplicationPart(typeof(ParfaitApp.Controllers.StoreController).Assembly)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("builder.Services.AddScoped<ParfaitProductService>()", program, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && File.Exists(Path.Combine(workspace, "MASTERAPP.sln"))) return workspace;
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MASTERAPP.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root unavailable.");
    }
}
