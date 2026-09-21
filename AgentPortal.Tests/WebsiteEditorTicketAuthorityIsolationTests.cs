using System;
using System.Collections.Generic;
using System.IO;
using Infrastructure.WebsiteEditing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteEditorTicketAuthorityIsolationTests
{
    [Fact]
    public void Production_DoesNotFallBackToGenericApplicationDataProtectionSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:BlobUri"] = "https://generic.example.test/container/keys.xml",
                ["DataProtection:KeyVaultKeyId"] = "https://generic-vault.example.test/keys/generic"
            })
            .Build();

        using var root = new TemporaryDirectory();
        var environment = new TestEnvironment(root.Path, Environments.Production);

        var error = Assert.Throws<InvalidOperationException>(
            () => WebsiteEditorTicketProtector.CreateShared(configuration, environment));

        Assert.Contains(WebsiteEditorTicketProtector.SharedBlobUriConfigKey, error.Message, StringComparison.Ordinal);
        Assert.Contains(WebsiteEditorTicketProtector.SharedKeyVaultKeyIdConfigKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_GenericApplicationDataProtectionSettingsCannotRedirectWebsiteTicketKeys()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "ClientApp"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:BlobUri"] = "https://unreachable.example.test/container/keys.xml",
                ["DataProtection:KeyVaultKeyId"] = "https://unreachable-vault.example.test/keys/generic"
            })
            .Build();

        var environment = new TestEnvironment(
            Path.Combine(root.Path, "ClientApp"),
            Environments.Development);

        using var protector = WebsiteEditorTicketProtector.CreateShared(configuration, environment);
        var ticket = new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Legend,
            WebsiteEditorSiteKeys.GlobalOwnerKey,
            null,
            true,
            DateTime.UtcNow.AddMinutes(5),
            null,
            ActorUserId: "authority-isolation-test",
            ActorEmail: "test@example.test");

        var protectedValue = protector.Protect(ticket);
        var roundTrip = protector.TryUnprotect(protectedValue);

        Assert.NotNull(roundTrip);
        Assert.Equal(ticket.ActorUserId, roundTrip!.ActorUserId);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "AgentPortal", "App_Data", "website-editor-keys")));
    }

    private sealed class TestEnvironment(string contentRootPath, string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "WebsiteEditorTicketAuthorityIsolationTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "legend-website-ticket-authority-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
