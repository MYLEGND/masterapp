using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Text.Json;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[CollectionDefinition("Founder operational diagnostic section", DisableParallelization = true)]
public sealed class FounderOperationalDiagnosticSectionCollection { }

[Collection("Founder operational diagnostic section")]
public sealed class LegendFounderOperationalDiagnosticSectionTests
{
    [Fact]
    public void Catalog_UsesStrictNullableSelectionFields()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var authority = BuildAuthority(db, new Mock<ILegendConnectOperations>(MockBehavior.Strict));
        Assert.Empty(LegendFounderToolAuthority.ValidateSerializedToolCatalog(authority.Tools));
        using var catalog = JsonSerializer.SerializeToDocument(authority.Tools);
        var tool = Assert.Single(catalog.RootElement.EnumerateArray(), item =>
            item.GetProperty("name").GetString() == "legend_operational_diagnostics");
        Assert.True(tool.GetProperty("strict").GetBoolean());
        var schema = tool.GetProperty("parameters");
        Assert.Equal(new[] { "section", "language" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"section\":null,\"language\":null}")]
    public async Task AggregateDefault_PreservesNativeReceiptAndStageNames(string arguments)
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedFounderAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(item => item.GetProviderCapacityAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Private capacity details"));
        var runtime = new Mock<ILegendConnectRuntimePolicyAuthority>(MockBehavior.Strict);
        runtime.Setup(item => item.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRuntimePolicySnapshot(false, 0, 0, 0, false, true, "Shadow", 0.98m, null, null, DateTime.UtcNow));
        runtime.Setup(item => item.GetReadinessAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Private readiness details"));
        var authority = BuildAuthority(db, operations, runtime.Object);
        var request = new LegendConnectReadOnlyContentBindingRequest(
            "request", "transition", "frame", "legend_operational_diagnostics", arguments,
            "runtimePolicy.contextualCompositionMode", null, 60, "$mode", "mode");
        var result = await authority.BindReadOnlyResultAsync(founder, request, CancellationToken.None);
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.NotNull(result.Receipt);
        operations.VerifyAll();
        runtime.VerifyAll();
    }

    [Fact]
    public async Task SelectedSection_UsesExistingFounderPageAuthorityWithoutAggregateReads()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedFounderAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(item => item.GetFounderSectionPageAsync("machine-learning-lifecycle", "en", null, null, null,
                It.IsAny<CancellationToken>()))
            .Returns((string section, string? language, string? search, string? cursor, Guid? family, CancellationToken token) =>
            {
                Assert.True(token.CanBeCanceled);
                return Task.FromResult(new LegendConnectFounderSectionPageSnapshot(section, language!, null, 25, null,
                    ["State"], [new[] { "Pending" }]));
            });
        var authority = BuildAuthority(db, operations);
        using var result = JsonDocument.Parse(await ExecuteAsync(authority, founder, SelectedArguments));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("en", result.RootElement.GetProperty("selectedSectionPage").GetProperty("languageCode").GetString());
        var stage = Assert.Single(result.RootElement.GetProperty("stages").EnumerateArray());
        Assert.Equal("selected_section_page", stage.GetProperty("name").GetString());
        Assert.Equal("available", stage.GetProperty("state").GetString());
        Assert.False(result.RootElement.TryGetProperty("productionReadiness", out _));
        operations.VerifyAll();
        operations.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("{\"section\":\"unknown\",\"language\":\"en\"}")]
    [InlineData("{\"section\":\"machine-learning-lifecycle\",\"language\":null}")]
    [InlineData("{\"section\":null,\"language\":\"en\"}")]
    [InlineData("{\"section\":null,\"section\":null,\"language\":null}")]
    [InlineData("{\"section\":\"machine-learning-lifecycle\",\"language\":\"en\",\"sql\":\"select private\"}")]
    [InlineData("[]")]
    public async Task InvalidSelection_FailsBeforeAnyRead(string arguments)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        using var result = JsonDocument.Parse(await ExecuteAsync(BuildAuthority(db, operations), ControllerTestHelpers.BuildUser(), arguments));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("operational_diagnostic_arguments_invalid", result.RootElement.GetProperty("error").GetString());
        Assert.Empty(operations.Invocations);
    }

    [Fact]
    public async Task SelectedFailure_IsNotSuccessAndDoesNotExposeExceptionMessageOrRows()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedFounderAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(item => item.GetFounderSectionPageAsync("machine-learning-lifecycle", "en", null, null, null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("password=SECRET; private row details"));
        var output = await ExecuteAsync(BuildAuthority(db, operations), founder, SelectedArguments);
        using var result = JsonDocument.Parse(output);
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("operational_diagnostic_stage_failed", result.RootElement.GetProperty("error").GetString());
        var stage = Assert.Single(result.RootElement.GetProperty("stages").EnumerateArray());
        Assert.Equal("InvalidOperationException", stage.GetProperty("exceptionType").GetString());
        Assert.False(result.RootElement.TryGetProperty("selectedSectionPage", out _));
        Assert.DoesNotContain("SECRET", output);
        Assert.DoesNotContain("private row", output);
    }

    [Fact]
    public async Task SelectedDeadline_ReportsFailedObservationWithinExistingBudget()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedFounderAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(item => item.GetFounderSectionPageAsync("machine-learning-lifecycle", "en", null, null, null,
                It.IsAny<CancellationToken>()))
            .Returns(async (string section, string? language, string? search, string? cursor, Guid? family, CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable");
            });
        using var result = JsonDocument.Parse(await ExecuteAsync(BuildAuthority(db, operations), founder, SelectedArguments));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("operational_diagnostic_stage_deadline_exceeded", result.RootElement.GetProperty("error").GetString());
        Assert.Equal("timed_out", Assert.Single(result.RootElement.GetProperty("stages").EnumerateArray()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ParentCancellation_PropagatesAndDoesNotReturnDiagnosticReceipt()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedFounderAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        operations.Setup(item => item.GetFounderSectionPageAsync("machine-learning-lifecycle", "en", null, null, null,
                It.IsAny<CancellationToken>()))
            .Returns((string section, string? language, string? search, string? cursor, Guid? family, CancellationToken token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<LegendConnectFounderSectionPageSnapshot>(token);
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(BuildAuthority(db, operations), founder,
            SelectedArguments, cancellation.Token));
    }

    [Fact]
    public async Task NonFounder_CannotReadSelectedSection()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ForbidResultException>(() => ExecuteAsync(BuildAuthority(db, operations),
            ControllerTestHelpers.BuildUser(Guid.NewGuid().ToString()), SelectedArguments));
        Assert.Empty(operations.Invocations);
    }

    [Theory]
    [InlineData("runtimePolicy.contextualCompositionMode", true)]
    [InlineData("productionReadiness.state", false)]
    public void AggregateReceipt_BindsOnlyAvailableStageFromSamePartialResponse(string path, bool expected)
    {
        const string output = """
            {"runtimePolicy":{"contextualCompositionMode":"Active"},
             "productionReadiness":{"state":"BLOCKED"},
             "stages":[{"name":"runtime_policy","state":"available"},
                       {"name":"production_readiness","state":"timed_out"}]}
            """;
        AssertDiagnosticReceipt(path, output, expected);
    }

    [Theory]
    [InlineData("available", true)]
    [InlineData("timed_out", false)]
    [InlineData("failed", false)]
    [InlineData("unavailable", false)]
    public void SelectedSectionReceipt_RequiresItsOwnAvailableStage(string state, bool expected)
    {
        var output = JsonSerializer.Serialize(new
        {
            selectedSectionPage = new { pageSize = 25 },
            stages = new[] { new { name = "selected_section_page", state } }
        });
        AssertDiagnosticReceipt("selectedSectionPage.pageSize", output, expected);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"production_readiness\",\"state\":\"available\"}]")]
    [InlineData("[{\"name\":\"selected_section_page\",\"state\":\"available\"},{\"name\":\"selected_section_page\",\"state\":\"failed\"}]")]
    public void SelectedSectionReceipt_RejectsMissingUnrelatedOrDuplicateStage(string stages)
    {
        var output = "{\"selectedSectionPage\":{\"pageSize\":25},\"stages\":" + stages + "}";
        AssertDiagnosticReceipt("selectedSectionPage.pageSize", output, false);
    }

    private static void AssertDiagnosticReceipt(string path, string output, bool expected)
    {
        var request = new LegendConnectReadOnlyContentBindingRequest(
            "request", "transition", "frame", "legend_operational_diagnostics",
            path.StartsWith("selectedSectionPage.", StringComparison.Ordinal) ? SelectedArguments : "{}",
            path, null, 60, "$value", "selected_value");
        var succeeded = LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            request, output, DateTime.UtcNow, out var receipt, out var reason);
        Assert.Equal(expected, succeeded);
        if (expected)
            Assert.NotNull(receipt);
        else
        {
            Assert.Null(receipt);
            Assert.Equal("read_only_content_binding_source_unavailable", reason);
        }
    }

    private const string SelectedArguments = "{\"section\":\"machine-learning-lifecycle\",\"language\":\"en\"}";
    private static Task<string> ExecuteAsync(LegendFounderToolAuthority authority, ClaimsPrincipal founder, string arguments,
        CancellationToken token = default) => authority.ExecuteAsync(founder,
        new FounderAiToolCall("section-read", "legend_operational_diagnostics", arguments), "teacher", token);

    private static LegendFounderToolAuthority BuildAuthority(MasterAppDbContext db, Mock<ILegendConnectOperations> operations,
        ILegendConnectRuntimePolicyAuthority? runtime = null) => new(
        new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db), runtimePolicy: runtime), null);

    private static async Task<ClaimsPrincipal> SeedFounderAsync(MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(), AgentUserId = FounderEnvironment.Oid,
            AgentUpn = "diagnostic-founder@legend.test", NormalizedEmail = "diagnostic-founder@legend.test", IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(FounderEnvironment.Oid);
    }

    private sealed class FounderEnvironment : IDisposable
    {
        public const string Oid = "11f6f9d9-0fe2-44c3-8cac-7d88d3fc3ac6";
        private readonly string? _previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        public FounderEnvironment() => Environment.SetEnvironmentVariable("FOUNDER_OID", Oid);
        public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }
}
