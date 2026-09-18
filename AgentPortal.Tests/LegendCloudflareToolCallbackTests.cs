using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class LegendCloudflareToolCallbackTests
{
    private const string FounderId = "587d1166-e29b-41d4-a716-446655440099";
    private const string ReadTool = "legend_software_remediation_status";
    private const string Nonce = "synthetic_nonce_with_32_characters";
    private static readonly byte[] SigningKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task SignedReadUsesPersistedScope_AndIdenticalReplayUsesOneDurableReadExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope();
        var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var first = await fixture.PostAsync(envelope, exactBody: body);
        var second = await fixture.PostAsync(envelope, exactBody: body);
        var receipt = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(first).Value);
        var replayReceipt = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(second).Value);
        Assert.Equal(receipt.GetRawText(), replayReceipt.GetRawText());
        Assert.Equal("legend-tool-receipt.v1", receipt.GetProperty("version").GetString());
        Assert.Equal(fixture.Scope.RequestId, receipt.GetProperty("requestId").GetString());
        Assert.True(receipt.GetProperty("reauthorized").GetBoolean());
        Assert.Equal("SyntheticReadOnlyStatus", receipt.GetProperty("output").GetProperty("authority").GetString());
        Assert.False(receipt.GetProperty("usage").GetProperty("known").GetBoolean());
        Assert.Equal(1000, receipt.GetProperty("usage").GetProperty("costMicrousd").GetInt32());
        Assert.Equal("reserved_upper_bound", receipt.GetProperty("usage").GetProperty("costEvidence").GetString());
        // Replays still reauthorize but reuse the existing durable execution
        // receipt. A read receipt is never Founder consent for a mutation.
        fixture.Remediation.Verify(value => value.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Remediation.VerifyNoOtherCalls();
        var row = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("ReadExecution", row.AuthorizationKind);
        Assert.Equal("Completed", row.State);
        Assert.Equal(ReadTool, row.ToolName);
        Assert.Equal(FounderId, row.UserId);
        Assert.Equal(Guid.Parse(fixture.Scope.RequestId), row.RequestId);
        Assert.Equal(Guid.Parse(fixture.Scope.ConversationId), row.ConversationId);
        Assert.Equal(envelope["actionDigest"]!.GetValue<string>(), row.ActionDigest);
        Assert.Equal(envelope["idempotencyKey"]!.GetValue<string>(), row.IdempotencyKey);
        Assert.Equal(receipt.GetProperty("output").GetRawText(), row.ResultJson);
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task MissingOrInvalidCostReservationCannotExecuteTool()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var amount in new long?[] { null, 0, -1, 1_000_000_001 })
        {
            var envelope = fixture.Envelope();
            if (amount is null) envelope.Remove("maxCostMicrousd");
            else envelope["maxCostMicrousd"] = amount.Value;
            Assert.IsType<BadRequestObjectResult>(await fixture.PostAsync(envelope));
        }
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    [Fact]
    public async Task SignedCapabilitiesArrayRemainsArray_AndReplaysItsDurableReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope(tool: "legend_capabilities");
        var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var first = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await fixture.PostAsync(envelope, exactBody: body)).Value);
        var replay = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await fixture.PostAsync(envelope, exactBody: body)).Value);

        // Valid tools can return arrays. The callback's authorization-error
        // check must not require every successful payload to be an object.
        var output = first.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        Assert.True(output.GetArrayLength() > 0);
        Assert.True(first.GetProperty("reauthorized").GetBoolean());
        Assert.Equal(first.GetRawText(), replay.GetRawText());
        var receipt = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("legend_capabilities", receipt.ToolName);
        Assert.Equal("ReadExecution", receipt.AuthorizationKind);
        Assert.Equal("Completed", receipt.State);
        Assert.Equal(output.GetRawText(), receipt.ResultJson);
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("tenant")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("roles")]
    [InlineData("role-order")]
    [InlineData("version")]
    [InlineData("environment")]
    [InlineData("conversation")]
    [InlineData("request")]
    public async Task ValidServiceSignatureCannotForgePersistedFounderScope(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        var changed = field switch
        {
            "account" => fixture.Scope with { AccountId = "other-account" },
            "tenant" => fixture.Scope with { TenantId = "other-tenant" },
            "user" => fixture.Scope with { UserId = Guid.NewGuid().ToString("D") },
            "session" => fixture.Scope with { SessionId = "other-session" },
            "roles" => fixture.Scope with { Roles = new[] { "Founder", "Administrator" } },
            "role-order" => fixture.Scope with { Roles = fixture.Scope.Roles.Reverse().ToArray() },
            "version" => fixture.Scope with { AuthorizationVersion = "other-version" },
            "environment" => fixture.Scope with { Environment = "other-environment" },
            "conversation" => fixture.Scope with { ConversationId = Guid.NewGuid().ToString("D") },
            _ => fixture.Scope with { RequestId = Guid.NewGuid().ToString("D") }
        };
        // Recompute the action digest and wire HMAC for the forged scope:
        // denial must come from persisted authorization, not a stale signature.
        var result = await fixture.PostAsync(fixture.Envelope(changed));
        Assert.Equal(403, Status(result));
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-key")]
    [InlineData("tampered-body")]
    public async Task BrowserFounderPrincipalCannotSubstituteForServiceSignature(string signature)
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope();
        var result = await fixture.PostAsync(envelope, signatureMode: signature,
            browserPrincipal: ControllerTestHelpers.BuildUser(FounderId));
        Assert.Equal(401, Status(result));
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    [Fact]
    public async Task SignedServiceUsesStoredFounderIdentity_AndDoesNotInheritOtherBrowserPrincipal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.PostAsync(fixture.Envelope(), browserPrincipal:
            ControllerTestHelpers.BuildUser(Guid.NewGuid().ToString("D")));
        Assert.IsType<OkObjectResult>(result);
        fixture.Remediation.Verify(value => value.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("issued-mismatch")]
    [InlineData("overlong")]
    [InlineData("stale-header")]
    [InlineData("future-header")]
    [InlineData("minimum-timestamp")]
    [InlineData("maximum-timestamp")]
    public async Task SignedExpiredOrInvalidBodyAndHeaderTimesCannotReachTool(string invalid)
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope();
        var timestamp = envelope["issuedAt"]!.GetValue<long>();
        if (invalid == "expired") envelope["expiresAt"] = timestamp - 1;
        if (invalid == "issued-mismatch") envelope["issuedAt"] = timestamp - 1;
        if (invalid == "overlong") envelope["expiresAt"] = timestamp + 30001;
        if (invalid == "stale-header") timestamp -= 60000;
        if (invalid == "future-header") timestamp += 60000;
        if (invalid == "minimum-timestamp") timestamp = long.MinValue;
        if (invalid == "maximum-timestamp") timestamp = long.MaxValue;
        var result = await fixture.PostAsync(envelope, headerTimestamp: timestamp);
        Assert.Equal(401, Status(result));
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("terminal")]
    [InlineData("closed")]
    public async Task ExactSignedReadReplayIsDeniedAfterCanonicalAuthorityEnds(string end)
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope();
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        Assert.IsType<OkObjectResult>(await fixture.PostAsync(envelope, exactBody: bytes));
        if (end == "profile") (await fixture.Db.AgentProfiles.SingleAsync()).IsActive = false;
        if (end == "closed") (await fixture.Db.MessageConversations.SingleAsync()).IsClosed = true;
        if (end == "terminal")
        {
            using var services = fixture.Scopes.CreateScope();
            Assert.True((await services.ServiceProvider.GetRequiredService<IMessagingService>().CompleteFounderAiTurnAsync(new(
                new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(fixture.Scope.ConversationId), Guid.Parse(fixture.Scope.RequestId),
                fixture.UserMessageId, "Completed.", MessagingAuthorKinds.Assistant, new(true, "legend")))).Succeeded);
        }
        await fixture.Db.SaveChangesAsync();
        var replay = await fixture.PostAsync(envelope, exactBody: bytes);
        Assert.Equal(403, Status(replay));
        fixture.Remediation.Verify(value => value.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.Remediation.VerifyNoOtherCalls();
        var row = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal("ReadExecution", row.AuthorizationKind);
        Assert.Equal("Completed", row.State);
    }

    [Fact]
    public async Task RevocationBetweenLookupAndToolReauthorizationNeverClaimsSuccessfulReauthorization()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.PostAsync(fixture.Envelope(), afterLookup: async () =>
        {
            (await fixture.Db.AgentProfiles.SingleAsync()).IsActive = false;
            await fixture.Db.SaveChangesAsync();
        });
        Assert.Equal(403, Status(result));
        fixture.Remediation.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("idempotency")]
    public async Task SignedActionIdentityMismatchCannotExecuteRead(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        var envelope = fixture.Envelope();
        envelope[field == "digest" ? "actionDigest" : "idempotencyKey"] = new string('0', 64);
        var result = await fixture.PostAsync(envelope);
        Assert.Equal(403, Status(result));
        fixture.Remediation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SignedMutationDoesNotCreateItsOwnFounderApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.PostAsync(fixture.Envelope(tool: "legend_release_approved_repair",
            arguments: JsonSerializer.Serialize(new { pull_request_number = 42, head_sha = new string('a', 40) })));
        Assert.Equal(403, Status(result));
        Assert.Empty(await fixture.Db.FounderAiActionAuthorizations.ToListAsync());
        fixture.Remediation.VerifyNoOtherCalls();
    }

    private static int Status(IActionResult result) => result switch
    {
        StatusCodeResult status => status.StatusCode,
        ObjectResult value => value.StatusCode ?? 200,
        _ => throw new InvalidOperationException("Unexpected callback result type.")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _priorFounder;
        public MasterAppDbContext Db { get; }
        public IServiceScopeFactory Scopes { get; }
        public FounderAiActionScope Scope { get; }
        public IConfiguration Configuration { get; }
        public Guid UserMessageId { get; private set; }
        public Mock<IFounderSoftwareRemediationService> Remediation { get; } = new(MockBehavior.Strict);
        private Fixture(MasterAppDbContext db)
        {
            _priorFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            Db = db;
            Scopes = ControllerTestHelpers.BuildFounderHistoryScopes(db);
            Scope = new("account-fixture", "tenant-fixture", FounderId, "session-fixture",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), new[] { "Founder", "Agent" },
                "authorization-v1", "qualification", DateTime.UtcNow.AddSeconds(90), new string('a', 64));
            const string prefix = "LegendConnect:Foundation:Cloudflare:";
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [prefix + "ToolCallbackEnabled"] = "true", [prefix + "CallbackKeyId"] = "callback-fixture",
                [prefix + "CallbackSigningKey"] = Convert.ToBase64String(SigningKey),
                [prefix + "AccountId"] = Scope.AccountId, [prefix + "Environment"] = Scope.Environment
            }).Build();
            Remediation.Setup(value => value.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new { authority = "SyntheticReadOnlyStatus" });
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(ControllerTestHelpers.BuildDb());
            try
            {
                fixture.Db.AgentProfiles.Add(new AgentProfile { AgentUserId = FounderId, AgentUpn = "fixture@example.invalid", IsActive = true });
                await fixture.Db.SaveChangesAsync();
                using var services = fixture.Scopes.CreateScope();
                var scope = fixture.Scope;
                var result = await services.ServiceProvider.GetRequiredService<IMessagingService>().BeginFounderAiTurnAsync(new(
                    new(FounderId, MessagingParticipantTypes.Agent), Guid.Parse(scope.ConversationId), Guid.Parse(scope.RequestId),
                    null, "Synthetic callback request.", "legend", scope.RequestFingerprint, scope.ExpiresUtc,
                    new(scope.AccountId, scope.TenantId, scope.UserId, scope.SessionId, scope.Roles, scope.AuthorizationVersion, scope.Environment, scope.ExpiresUtc)));
                Assert.True(result.Succeeded, result.ErrorMessage);
                fixture.UserMessageId = result.UserMessage!.Id;
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public JsonObject Envelope(FounderAiActionScope? forged = null, string tool = ReadTool, string arguments = "{}")
        {
            var scope = forged ?? Scope;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return JsonSerializer.SerializeToNode(new
            {
                version = "legend-tool-callback.v1", requestId = scope.RequestId, environment = scope.Environment,
                issuedAt = timestamp, expiresAt = timestamp + 25000, maxCostMicrousd = 1000, contextDigest = new string('c', 64),
                scope = new { accountId = scope.AccountId, tenantId = scope.TenantId, userId = scope.UserId,
                    sessionId = scope.SessionId, conversationId = scope.ConversationId, roles = scope.Roles,
                    authorizationVersion = scope.AuthorizationVersion },
                call = new { id = "call-fixture", name = tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) },
                actionDigest = LegendFounderToolAuthority.ComputeCloudActionDigest(scope, tool, arguments),
                idempotencyKey = LegendFounderToolAuthority.ComputeCloudToolIdempotencyKey(scope.RequestId, "call-fixture")
            })!.AsObject();
        }
        public async Task<IActionResult> PostAsync(JsonObject envelope, string signatureMode = "valid", byte[]? exactBody = null,
            long? headerTimestamp = null, ClaimsPrincipal? browserPrincipal = null, Func<Task>? afterLookup = null)
        {
            var bytes = exactBody ?? Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var timestamp = headerTimestamp ?? envelope["issuedAt"]!.GetValue<long>();
            var key = signatureMode == "wrong-key" ? new byte[32] : SigningKey;
            var signature = LegendCloudflareServiceSignature.Sign(key, "POST", LegendCloudflareToolController.CallbackPath,
                "callback-fixture", timestamp, Nonce, bytes);
            var context = new DefaultHttpContext();
            context.User = browserPrincipal ?? new ClaimsPrincipal(new ClaimsIdentity());
            context.Request.Method = "POST";
            context.Request.Path = LegendCloudflareToolController.CallbackPath;
            context.Request.ContentType = "application/json";
            context.Request.Headers["X-Legend-Key-Id"] = "callback-fixture";
            context.Request.Headers["X-Legend-Nonce"] = Nonce;
            context.Request.Headers["X-Legend-Timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (signatureMode != "missing") context.Request.Headers["X-Legend-Signature"] = signature;
            context.Request.Body = new MemoryStream(signatureMode == "tampered-body" ? bytes.Concat(new byte[] { 32 }).ToArray() : bytes);
            using var services = Scopes.CreateScope();
            var messaging = services.ServiceProvider.GetRequiredService<IMessagingService>();
            if (afterLookup is not null)
            {
                var original = messaging;
                var intercepted = new Mock<IMessagingService>(MockBehavior.Strict);
                intercepted.Setup(value => value.GetFounderAiOperationDelegationAsync(It.IsAny<MessagingActor>(),
                        It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(async (MessagingActor actor, Guid conversation, Guid operation, CancellationToken token) =>
                    {
                        var stored = await original.GetFounderAiOperationDelegationAsync(actor, conversation, operation, token);
                        await afterLookup();
                        return stored;
                    });
                messaging = intercepted.Object;
            }
            var controller = new LegendCloudflareToolController(Configuration, messaging,
                new FounderLegendConnectService(Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(Db)),
                Remediation.Object, Scopes) { ControllerContext = new ControllerContext { HttpContext = context } };
            var result = await controller.Execute(CancellationToken.None);
            Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Environment.SetEnvironmentVariable("FOUNDER_OID", _priorFounder);
        }
    }
}
