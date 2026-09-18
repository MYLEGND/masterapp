using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderCloudToolExposureTests
{
    private const string FounderId = "587d1166-e29b-41d4-a716-446655440099";

    [Fact]
    public async Task CloudCatalogReusesExactExistingSchemas_AndExposesOnlyAuditedReads()
    {
        await using var fixture = await Fixture.CreateAsync();
        var authority = fixture.Authority();
        var original = authority.GetAvailableTools(false, fixture.Scope.ConversationId,
                LegendConnectExternalProviderPolicy.CloudflareFoundation, false)
            .Select(value => JsonSerializer.SerializeToElement(value))
            .ToDictionary(value => value.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var exposed = authority.GetAvailableCloudTools(fixture.Scope.ConversationId,
                LegendConnectExternalProviderPolicy.CloudflareFoundation)
            .Select(value => JsonSerializer.SerializeToElement(value)).ToArray();
        Assert.Equal(new[]
        {
            "legend_calculate", "legend_capabilities", "legend_client_lead_portfolio", "legend_inspect_repository",
            "legend_provider_capacity", "legend_software_remediation_status", "legend_system_overview"
        }, exposed.Select(value => value.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        foreach (var schema in exposed)
        {
            var name = schema.GetProperty("name").GetString()!;
            Assert.True(authority.IsReadOnly(name));
            Assert.Equal(original[name].GetRawText(), schema.GetRawText());
        }
        Assert.Empty(authority.GetAvailableCloudTools(fixture.Scope.ConversationId, LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.Empty(authority.GetAvailableCloudTools(fixture.Scope.ConversationId, LegendConnectExternalProviderPolicy.ProviderEnabled));
        fixture.Operations.VerifyNoOtherCalls();
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    [Theory]
    [InlineData("legend_metric_detail", "{\"metric_key\":\"provider-operations\"}")]
    [InlineData("legend_language_knowledge", "{\"language\":\"en\"}")]
    [InlineData("legend_pair_health", "{\"pair\":\"en-es\"}")]
    [InlineData("legend_translation_quality", "{}")]
    [InlineData("legend_target_realizations", "{}")]
    [InlineData("legend_search_retained_knowledge", "{\"query\":\"private\",\"source_language\":null,\"target_language\":null}")]
    [InlineData("legend_language_state", "{\"language\":\"en\",\"pair\":null}")]
    [InlineData("legend_operational_diagnostics", "{}")]
    [InlineData("legend_research_internet", "{}")]
    [InlineData("legend_request_teacher_escalation", "{}")]
    [InlineData("legend_inspect_repair_validation", "{\"pull_request_number\":42,\"head_sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("legend_request_repair_release", "{\"pull_request_number\":42,\"head_sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("legend_verify_repair_deployment", "{\"commit_sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("legend_future_sensitive_read", "{}")]
    public async Task SignedValidScopeCannotInvokeUnexposedRead_OrCreateExecutionLedger(string name, string arguments)
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = Assert.IsType<ObjectResult>(await fixture.CallbackAsync(name, arguments));
        Assert.Equal(403, response.StatusCode);
        Assert.Equal("cloud_action_tool_not_exposed", JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetString());
        fixture.Operations.VerifyNoOtherCalls();
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task CloudCapabilitiesReportOnlyTheCurrentlyExposedCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = Assert.IsType<OkObjectResult>(await fixture.CallbackAsync("legend_capabilities", "{}"));
        var output = JsonSerializer.SerializeToElement(response.Value).GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        var names = output.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Contains("legend_calculate", names);
        Assert.DoesNotContain("legend_metric_detail", names);
        Assert.DoesNotContain("legend_prepare_software_repair", names);
        Assert.Equal(7, names.Length);
    }

    [Fact]
    public async Task AuditedCalculationStillExecutesThroughTheExistingSignedCallbackAndReadLedger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var response = Assert.IsType<OkObjectResult>(await fixture.CallbackAsync("legend_calculate",
            "{\"operation\":\"add\",\"left\":\"17\",\"right\":\"6\"}"));
        var json = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal("23", json.GetProperty("output").GetProperty("result").GetString());
        Assert.True(json.GetProperty("reauthorized").GetBoolean());
        var row = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("ReadExecution", row.AuthorizationKind);
        Assert.Equal("Completed", row.State);
        fixture.Operations.VerifyNoOtherCalls();
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NonexposedMutationRetainsExistingExactFounderApprovalBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string tool = "legend_release_approved_repair";
        var arguments = JsonSerializer.Serialize(new { pull_request_number = 42, head_sha = new string('a', 40) });
        fixture.Remediation.Setup(value => value.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { publicationRequested = true, released = false });
        var denied = Assert.IsType<ObjectResult>(await fixture.CallbackAsync(tool, arguments));
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal("cloud_action_approval_required", JsonSerializer.SerializeToElement(denied.Value).GetProperty("error").GetString());
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
        var approval = await fixture.Authority().IssueCloudActionApprovalAsync(fixture.Principal,
            fixture.Scope, tool, arguments, fixture.Scope.ExpiresUtc, CancellationToken.None);
        Assert.True(approval.Succeeded, approval.Error);
        var response = Assert.IsType<OkObjectResult>(await fixture.CallbackAsync(tool, arguments));
        Assert.True(JsonSerializer.SerializeToElement(response.Value).GetProperty("output").GetProperty("publicationRequested").GetBoolean());
        fixture.Remediation.Verify(value => value.ReleaseApprovedAsync(42, new string('a', 40), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Equal("FounderApproval", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync()).AuthorizationKind);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        private readonly byte[] _key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        public MasterAppDbContext Db { get; } = ControllerTestHelpers.BuildDb();
        public IServiceScopeFactory Scopes { get; }
        public FounderAiActionScope Scope { get; }
        public ClaimsPrincipal Principal { get; }
        public Mock<ILegendConnectOperations> Operations { get; } = new(MockBehavior.Strict);
        public Mock<IFounderSoftwareRemediationService> Remediation { get; } = new(MockBehavior.Strict);
        private Fixture()
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            Scopes = ControllerTestHelpers.BuildFounderHistoryScopes(Db);
            Scope = new("account-fixture", "tenant-fixture", FounderId, "session-fixture",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), new[] { "Founder", "Agent" },
                "authorization-v1", "qualification", DateTime.UtcNow.AddSeconds(90), new string('a', 64));
            Principal = ControllerTestHelpers.BuildUser(FounderId);
            ((ClaimsIdentity)Principal.Identity!).AddClaim(new Claim("sid", Scope.SessionId));
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                fixture.Db.AgentProfiles.Add(new AgentProfile { AgentUserId = FounderId, AgentUpn = "fixture@example.invalid", IsActive = true });
                await fixture.Db.SaveChangesAsync();
                using var services = fixture.Scopes.CreateScope();
                var scope = fixture.Scope;
                var begun = await services.ServiceProvider.GetRequiredService<IMessagingService>().BeginFounderAiTurnAsync(new(
                    new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(scope.ConversationId), Guid.Parse(scope.RequestId),
                    null, "Synthetic cloud exposure request.", "legend", scope.RequestFingerprint, scope.ExpiresUtc,
                    new(scope.AccountId, scope.TenantId, scope.UserId, scope.SessionId, scope.Roles, scope.AuthorizationVersion, scope.Environment, scope.ExpiresUtc)));
                Assert.True(begun.Succeeded, begun.ErrorMessage);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public LegendFounderToolAuthority Authority() => new(Legend(), Remediation.Object, authorizationScopes: Scopes);
        private FounderLegendConnectService Legend() => new(Operations.Object, new AgentProfileAccessResolver(Db));
        public async Task<IActionResult> CallbackAsync(string name, string arguments)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string nonce = "synthetic_nonce_with_32_characters";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "legend-tool-callback.v1", requestId = Scope.RequestId, environment = Scope.Environment,
                issuedAt = timestamp, expiresAt = timestamp + 25000, maxCostMicrousd = 1000, contextDigest = new string('c', 64),
                scope = new { accountId = Scope.AccountId, tenantId = Scope.TenantId, userId = Scope.UserId,
                    sessionId = Scope.SessionId, conversationId = Scope.ConversationId, roles = Scope.Roles,
                    authorizationVersion = Scope.AuthorizationVersion },
                call = new { id = "call-fixture", name, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) },
                actionDigest = LegendFounderToolAuthority.ComputeCloudActionDigest(Scope, name, arguments),
                idempotencyKey = LegendFounderToolAuthority.ComputeCloudToolIdempotencyKey(Scope.RequestId, "call-fixture")
            });
            const string prefix = "LegendConnect:Foundation:Cloudflare:";
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [prefix + "ToolCallbackEnabled"] = "true", [prefix + "CallbackKeyId"] = "fixture",
                [prefix + "CallbackSigningKey"] = Convert.ToBase64String(_key),
                [prefix + "AccountId"] = Scope.AccountId, [prefix + "Environment"] = Scope.Environment
            }).Build();
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(bytes);
            context.Request.Headers["X-Legend-Key-Id"] = "fixture";
            context.Request.Headers["X-Legend-Timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Headers["X-Legend-Nonce"] = nonce;
            context.Request.Headers["X-Legend-Signature"] = LegendCloudflareServiceSignature.Sign(_key, "POST",
                LegendCloudflareToolController.CallbackPath, "fixture", timestamp, nonce, bytes);
            using var services = Scopes.CreateScope();
            var controller = new LegendCloudflareToolController(config, services.ServiceProvider.GetRequiredService<IMessagingService>(),
                Legend(), Remediation.Object, Scopes) { ControllerContext = new ControllerContext { HttpContext = context } };
            return await controller.Execute(CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Environment.SetEnvironmentVariable("FOUNDER_OID", _priorFounder);
        }
    }
}
