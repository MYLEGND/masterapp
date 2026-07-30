using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shared.Security;
using Xunit;

namespace AgentPortal.Tests;

// Phase 4 — Platform Security Infrastructure regression coverage.
//   Objective 1: shared Data Protection authority (persistence + app-name isolation).
//   Objective 2: shared security-header + forwarded-header middleware.
//   Objective 3: startup/configuration validation (fail fast, dev-safe, no secrets).
public class Phase4PlatformSecurityTests
{
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // =====================================================================
    // OBJECTIVE 1 — Data Protection authority
    // =====================================================================

    [Fact] // Keys persist (file-system fallback) and protect/unprotect round-trips.
    public void DataProtection_FileSystemFallback_RoundTrips()
    {
        var keysDir = Path.Combine(Path.GetTempPath(), "mp-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = BuildProvider("RoundTripApp", keysDir);
            var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("phase4.test.v1");

            var cipher = protector.Protect("sensitive-value");
            Assert.NotEqual("sensitive-value", cipher);
            Assert.Equal("sensitive-value", protector.Unprotect(cipher));

            // Keys were actually persisted to the configured directory.
            Assert.True(Directory.Exists(keysDir));
        }
        finally
        {
            TryDelete(keysDir);
        }
    }

    [Fact] // Application-name isolation: a different app name cannot unprotect another app's data.
    public void DataProtection_ApplicationName_IsolatesPurposes()
    {
        // Same key-ring directory, different application names => isolated.
        var keysDir = Path.Combine(Path.GetTempPath(), "mp-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var appA = BuildProvider("AppA", keysDir)
                .GetRequiredService<IDataProtectionProvider>().CreateProtector("shared.purpose");
            var appB = BuildProvider("AppB", keysDir)
                .GetRequiredService<IDataProtectionProvider>().CreateProtector("shared.purpose");

            var cipher = appA.Protect("agent-scoped-secret");

            // AppB (different application name) must not be able to read AppA's data.
            Assert.ThrowsAny<CryptographicException>(() => appB.Unprotect(cipher));
        }
        finally
        {
            TryDelete(keysDir);
        }
    }

    [Fact] // The same application name CAN decrypt (the intentional AgentPortal/Protect-Website sharing model).
    public void DataProtection_SameApplicationName_CanCrossDecrypt()
    {
        var keysDir = Path.Combine(Path.GetTempPath(), "mp-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = BuildProvider("AgentPortal", keysDir)
                .GetRequiredService<IDataProtectionProvider>().CreateProtector("MetaCapi.v1");
            var reader = BuildProvider("AgentPortal", keysDir)
                .GetRequiredService<IDataProtectionProvider>().CreateProtector("MetaCapi.v1");

            var cipher = writer.Protect("meta-capi-token");
            Assert.Equal("meta-capi-token", reader.Unprotect(cipher));
        }
        finally
        {
            TryDelete(keysDir);
        }
    }

    [Fact]
    public void DataProtection_RequiresApplicationName()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddPlatformDataProtection(Config(), new TestHostEnvironment(), applicationName: ""));
    }

    private static ServiceProvider BuildProvider(string applicationName, string keysDir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformDataProtection(
            Config(),                       // empty config => file-system fallback
            new TestHostEnvironment(),
            applicationName,
            developmentKeysDirectory: keysDir);
        return services.BuildServiceProvider();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // =====================================================================
    // OBJECTIVE 2 — Security + forwarded headers
    // =====================================================================

    [Fact]
    public async Task SecurityHeaders_ArePresent_AndNonDestructive()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UsePlatformSecurityHeaders();
                    app.Run(async ctx =>
                    {
                        if (ctx.Request.Path == "/custom")
                            ctx.Response.Headers["X-Frame-Options"] = "DENY"; // app-specific value
                        await ctx.Response.WriteAsync("ok");
                    });
                }))
            .StartAsync();

        var client = host.GetTestClient();

        var baseline = await client.GetAsync("/");
        Assert.Equal("nosniff", string.Join(",", baseline.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("SAMEORIGIN", string.Join(",", baseline.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("strict-origin-when-cross-origin", string.Join(",", baseline.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("geolocation=()", string.Join(",", baseline.Headers.GetValues("Permissions-Policy")));

        // Non-destructive: an app that already set the header keeps its own value.
        var custom = await client.GetAsync("/custom");
        Assert.Equal("DENY", string.Join(",", custom.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public void ForwardedHeaders_ConfiguredForXForwardedForAndProto()
    {
        var services = new ServiceCollection();
        services.AddPlatformForwardedHeaders();
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    // =====================================================================
    // OBJECTIVE 3 — Startup / configuration validation
    // =====================================================================

    [Fact]
    public void ValidateDataProtection_BothSetInProduction_DoesNotThrow()
    {
        var cfg = Config(
            ("DataProtection:BlobUri", "https://example.blob.core.windows.net/keys"),
            ("DataProtection:KeyVaultKeyId", "https://example.vault.azure.net/keys/dp"));
        PlatformConfigValidation.ValidateDataProtection(cfg, isProduction: true);
    }

    [Fact]
    public void ValidateDataProtection_NeitherSetInProduction_DoesNotThrow()
    {
        PlatformConfigValidation.ValidateDataProtection(Config(), isProduction: true);
    }

    [Fact]
    public void ValidateDataProtection_PartialInProduction_Throws()
    {
        var onlyBlob = Config(("DataProtection:BlobUri", "https://example.blob.core.windows.net/keys"));
        var ex1 = Assert.Throws<InvalidOperationException>(() =>
            PlatformConfigValidation.ValidateDataProtection(onlyBlob, isProduction: true));

        var onlyKey = Config(("DataProtection:KeyVaultKeyId", "https://example.vault.azure.net/keys/dp"));
        Assert.Throws<InvalidOperationException>(() =>
            PlatformConfigValidation.ValidateDataProtection(onlyKey, isProduction: true));

        // Message references setting NAMES, never a value.
        Assert.Contains("DataProtection:BlobUri", ex1.Message);
        Assert.DoesNotContain("example.blob.core.windows.net", ex1.Message);
    }

    [Fact]
    public void ValidateDataProtection_PartialInDevelopment_DoesNotThrow()
    {
        var onlyBlob = Config(("DataProtection:BlobUri", "https://example.blob.core.windows.net/keys"));
        PlatformConfigValidation.ValidateDataProtection(onlyBlob, isProduction: false);
    }

    [Fact]
    public void RequireInProduction_MissingInProduction_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PlatformConfigValidation.RequireInProduction(null, isProduction: true, "Some:Setting"));
        Assert.Throws<InvalidOperationException>(() =>
            PlatformConfigValidation.RequireInProduction("   ", isProduction: true, "Some:Setting"));
    }

    [Fact]
    public void RequireInProduction_PresentOrDevelopment_DoesNotThrow()
    {
        PlatformConfigValidation.RequireInProduction("value", isProduction: true, "Some:Setting");
        PlatformConfigValidation.RequireInProduction(null, isProduction: false, "Some:Setting");
    }
}
