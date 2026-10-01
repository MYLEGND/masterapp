using AgentPortal.Services;
using AgentPortal.Services.Engineering;
using Domain.Engineering;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendEngineeringBrowserIngestionTests
{
    [Fact]
    public void SameHostBrowserIncident_UsesServerRevisionAndAppScopedRepairAuthority()
    {
        using var fixture = BrowserFixture.Create("AgentPortal");
        var serverRevision = new string('a', 40);
        var advertisedRevision = new string('f', 40);

        var incident = RuntimeDiagnosticSanitizer.Sanitize(
            BrowserEvent("AgentPortal", "/js/application.js", advertisedRevision),
            false,
            fixture.Environment,
            fixture.Context,
            Array.Empty<EndpointDataSource>(),
            DateTime.UtcNow,
            serverRevision);

        Assert.True(incident.ReleaseVerified);
        Assert.Equal(serverRevision, incident.GitCommitHash);
        Assert.NotEqual(advertisedRevision, incident.GitCommitHash);
        Assert.Equal("/js/application.js", incident.SourceFilePath);

        var decision = LegendEngineeringPolicies.Classify(incident);
        Assert.Equal(EngineeringFailureClass.CodeDefect, decision.FailureClass);
        Assert.True(decision.CodeRepairEligible);
        Assert.Equal(EngineeringRiskClass.TierA, decision.RiskClass);
        Assert.Equal("source:AgentPortal:wwwroot/js/application.js", decision.CanonicalAuthorityKey);
        Assert.Equal(new[] { "AgentPortal" }, decision.AffectedProjects);
        Assert.Contains("app:AgentPortal", decision.ImpactSet);

        var resolved = FounderSoftwareRemediationService.ResolveSafeSourceHints(
            new[] { incident.SourceFilePath!.TrimStart('/') },
            "AgentPortal",
            new[]
            {
                "AgentPortal/wwwroot/js/application.js",
                "ClientApp/wwwroot/js/application.js",
                "SHARED/wwwroot/js/application.js"
            });

        Assert.Equal(new[] { "AgentPortal/wwwroot/js/application.js" }, resolved);
    }

    [Fact]
    public void CrossHostStaticObservation_CannotBorrowApiHostReleaseAuthority()
    {
        using var fixture = BrowserFixture.Create("ProtectWebsite");
        var serverRevision = new string('a', 40);
        var advertisedRevision = new string('f', 40);

        var incident = RuntimeDiagnosticSanitizer.Sanitize(
            BrowserEvent("Legend-Website", "/js/application.js", advertisedRevision),
            false,
            fixture.Environment,
            fixture.Context,
            Array.Empty<EndpointDataSource>(),
            DateTime.UtcNow,
            serverRevision);

        Assert.False(incident.ReleaseVerified);
        Assert.Equal(advertisedRevision, incident.GitCommitHash);
        var decision = LegendEngineeringPolicies.Classify(incident);
        Assert.NotEqual(EngineeringFailureClass.CodeDefect, decision.FailureClass);
        Assert.False(decision.CodeRepairEligible);
        Assert.Equal(EngineeringRiskClass.TierC, decision.RiskClass);
    }

    [Fact]
    public void SharedStaticBrowserIncident_ResolvesToSingleSharedRepositoryAuthority()
    {
        using var fixture = BrowserFixture.Create(
            "AgentPortal",
            "_content/Shared/js/page-health.js");
        var incident = RuntimeDiagnosticSanitizer.Sanitize(
            BrowserEvent("AgentPortal", "/_content/Shared/js/page-health.js", new string('f', 40)),
            false,
            fixture.Environment,
            fixture.Context,
            Array.Empty<EndpointDataSource>(),
            DateTime.UtcNow,
            new string('a', 40));

        Assert.True(incident.ReleaseVerified);
        var decision = LegendEngineeringPolicies.Classify(incident);
        Assert.Equal(EngineeringFailureClass.CodeDefect, decision.FailureClass);
        Assert.Equal("source:SHARED:wwwroot/js/page-health.js", decision.CanonicalAuthorityKey);
        Assert.Equal(new[] { "SHARED" }, decision.AffectedProjects);
        Assert.Contains("shared:*", decision.ImpactSet);

        var resolved = FounderSoftwareRemediationService.ResolveSafeSourceHints(
            new[] { incident.SourceFilePath!.TrimStart('/') },
            "AgentPortal",
            new[]
            {
                "AgentPortal/wwwroot/js/page-health.js",
                "SHARED/wwwroot/js/page-health.js"
            });

        Assert.Equal(new[] { "SHARED/wwwroot/js/page-health.js" }, resolved);
    }

    private static RuntimeDiagnosticEvent BrowserEvent(string application, string source, string advertisedRevision) =>
        new()
        {
            AppIdentifier = application,
            Platform = "Web",
            Route = "/Clients/Index",
            SourceFilePath = source,
            ErrorName = "TypeError",
            GitCommitHash = advertisedRevision,
            StructuralReproducer = new RuntimeDiagnosticStructuralReproducer
            {
                ComponentIds = new[] { "website.modal" },
                ActionKeys = new[] { "contact.submit" },
                CompositionIds = new[] { "cms.hero.primary" },
                ModalIds = new[] { "website-modal" }
            }
        };

    private sealed class BrowserFixture : IDisposable
    {
        private readonly string _directory;
        private readonly PhysicalFileProvider _files;

        private BrowserFixture(string directory, string application)
        {
            _directory = directory;
            _files = new PhysicalFileProvider(directory);
            Environment = new WebEnvironmentStub
            {
                ApplicationName = application,
                WebRootPath = directory,
                WebRootFileProvider = _files
            };
            Context = new DefaultHttpContext();
            Context.Request.Scheme = "https";
            Context.Request.Host = new HostString("fixture.mylegnd.test");
        }

        internal WebEnvironmentStub Environment { get; }
        internal DefaultHttpContext Context { get; }

        internal static BrowserFixture Create(string application, string relativeAsset = "js/application.js")
        {
            var directory = Path.Combine(Path.GetTempPath(), "legend-browser-ingestion-" + Guid.NewGuid().ToString("N"));
            var fullPath = Path.Combine(directory, relativeAsset.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "// structural browser source fixture");
            return new BrowserFixture(directory, application);
        }

        public void Dispose()
        {
            _files.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class WebEnvironmentStub : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AgentPortal";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "/";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
