using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

// Reuse the canonical messaging/history fixture, including its real Founder
// resource authority. This is not a second session or permission authority.
public sealed partial class MessagingServiceTests
{
    private static MessagingFounderAiCloudflareDelegation DelegationScope() => new(
        "account-fixture", "tenant-fixture", FounderTestObjectId, "session-fixture",
        new[] { "Founder", "Agent" }, "authorization-v1", "Production", DateTime.UtcNow.AddSeconds(90));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderAiDelegation_RoundTripsOnlyPersistedOwnedPendingScope_WithoutHistoryDisclosure(bool relational)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(relational);
        var scope = DelegationScope();
        var request = fixture.Request("A private Founder question.") with { CloudflareDelegation = scope };
        var started = await fixture.Service.BeginFounderAiTurnAsync(request);
        Assert.True(started.Succeeded, started.ErrorMessage);
        var stored = await fixture.Db.InternalMessages.AsNoTracking().SingleAsync();
        using var storedJson = JsonDocument.Parse(stored.AiTurnMetadataJson!);
        Assert.Equal(2, storedJson.RootElement.GetProperty("version").GetInt32());
        fixture.Db.ChangeTracker.Clear();
        var result = await fixture.NewService().GetFounderAiOperationDelegationAsync(fixture.Actor, request.ConversationId, request.OperationId);
        Assert.NotNull(result);
        Assert.Equal(request.ConversationId, result.ConversationId);
        Assert.Equal(request.OperationId, result.OperationId);
        Assert.Equal(request.RequestFingerprint, result.RequestFingerprint);
        Assert.Equal(request.ExecutionDeadlineUtc, result.ExecutionDeadlineUtc);
        Assert.Equal(JsonSerializer.Serialize(scope), JsonSerializer.Serialize(result.Delegation));
        Assert.DoesNotContain(request.Body, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var page = await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, request.ConversationId, new());
        Assert.True(page.Succeeded, page.ErrorMessage);
        var history = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("account-fixture", history, StringComparison.Ordinal);
        Assert.DoesNotContain("session-fixture", history, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-v1", history, StringComparison.Ordinal);
        Assert.Single(page.Conversation!.Messages);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
    }

    [Fact]
    public async Task FounderAiDelegation_LegacyV1HistoryAndReplayNeverGrantCallbackScope()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var request = fixture.Request("Legacy operation.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(request);
        Assert.True(started.Succeeded);
        var json = JsonNode.Parse((await fixture.Db.InternalMessages.SingleAsync()).AiTurnMetadataJson!)!;
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Null(json["cloudflareDelegation"]);
        Assert.Null(await fixture.Service.GetFounderAiOperationDelegationAsync(fixture.Actor, request.ConversationId, request.OperationId));
        Assert.Equal("Pending", (await fixture.Service.BeginFounderAiTurnAsync(request)).State);
        Assert.True((await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, request.ConversationId, new())).Succeeded);
        var injected = await fixture.Service.BeginFounderAiTurnAsync(request with { CloudflareDelegation = DelegationScope() });
        Assert.Equal("FOUNDER_HISTORY_REPLAY_MISMATCH", injected.ErrorCode);
        Assert.Equal(json.ToJsonString(), JsonNode.Parse((await fixture.Db.InternalMessages.SingleAsync()).AiTurnMetadataJson!)!.ToJsonString());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("tenant")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("environment")]
    [InlineData("roles")]
    [InlineData("wrong-role-case")]
    [InlineData("duplicate-roles")]
    [InlineData("oversized-role")]
    [InlineData("secret-shaped-identifier")]
    [InlineData("expired")]
    [InlineData("long-expiry")]
    [InlineData("non-utc")]
    [InlineData("past-execution-deadline")]
    public async Task FounderAiDelegation_InvalidScopeIsRejectedBeforeAnyHistoryMutation(string invalid)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var scope = DelegationScope();
        scope = invalid switch
        {
            "account" => scope with { AccountId = "" },
            "tenant" => scope with { TenantId = "" },
            "user" => scope with { UserId = Guid.NewGuid().ToString() },
            "session" => scope with { SessionId = "" },
            "version" => scope with { AuthorizationVersion = "" },
            "environment" => scope with { Environment = "" },
            "roles" => scope with { Roles = Array.Empty<string>() },
            "wrong-role-case" => scope with { Roles = new[] { "founder" } },
            "duplicate-roles" => scope with { Roles = new[] { "Founder", "Founder" } },
            "oversized-role" => scope with { Roles = new[] { "Founder", new string('r', 65) } },
            "secret-shaped-identifier" => scope with { SessionId = "Bearer synthetic credential" },
            "expired" => scope with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) },
            "long-expiry" => scope with { ExpiresUtc = DateTime.UtcNow.AddSeconds(121) },
            "non-utc" => scope with { ExpiresUtc = DateTime.SpecifyKind(scope.ExpiresUtc, DateTimeKind.Unspecified) },
            _ => scope
        };
        var request = fixture.Request("Must not create history.") with { CloudflareDelegation = scope };
        if (invalid == "past-execution-deadline") request = request with { ExecutionDeadlineUtc = DateTime.UtcNow.AddSeconds(30) };
        Assert.False((await fixture.Service.BeginFounderAiTurnAsync(request)).Succeeded);
        Assert.Empty(await fixture.Db.MessageConversations.ToListAsync());
        Assert.Empty(await fixture.Db.InternalMessages.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("closed")]
    [InlineData("inactive-profile")]
    [InlineData("wrong-user")]
    [InlineData("wrong-role")]
    [InlineData("wrong-conversation")]
    [InlineData("wrong-operation")]
    [InlineData("participant-revoked")]
    [InlineData("deleted-user-message")]
    [InlineData("scope-expired")]
    [InlineData("execution-expired")]
    [InlineData("stored-long-expiry")]
    [InlineData("stored-past-deadline")]
    [InlineData("stored-wrong-user")]
    [InlineData("missing-scope")]
    [InlineData("missing-role")]
    [InlineData("unknown-token-field")]
    public async Task FounderAiDelegation_ReadFailsClosedForTerminalRevokedForeignOrInvalidStoredScope(string change)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var request = fixture.Request("Scoped operation.") with { CloudflareDelegation = DelegationScope() };
        var started = await fixture.Service.BeginFounderAiTurnAsync(request);
        Assert.True(started.Succeeded);
        var actor = fixture.Actor;
        var conversationId = request.ConversationId;
        var operationId = request.OperationId;
        var message = await fixture.Db.InternalMessages.SingleAsync();
        switch (change)
        {
            case "finished":
                Assert.True((await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, conversationId,
                    operationId, started.UserMessage!.Id, "Completed.", MessagingAuthorKinds.Assistant, new(true, "legend")))).Succeeded);
                break;
            case "closed": (await fixture.Db.MessageConversations.SingleAsync()).IsClosed = true; break;
            case "inactive-profile": (await fixture.Db.AgentProfiles.SingleAsync()).IsActive = false; break;
            case "wrong-user": actor = new(Guid.NewGuid().ToString(), MessagingParticipantTypes.Agent); break;
            case "wrong-role": actor = actor with { ParticipantType = MessagingParticipantTypes.Client }; break;
            case "wrong-conversation": conversationId = Guid.NewGuid(); break;
            case "wrong-operation": operationId = Guid.NewGuid(); break;
            case "participant-revoked": (await fixture.Db.MessageConversationParticipants.SingleAsync()).IsActive = false; break;
            case "deleted-user-message": message.IsDeleted = true; break;
            default:
                var json = JsonNode.Parse(message.AiTurnMetadataJson!)!;
                if (change == "scope-expired") json["cloudflareDelegation"]!["expiresUtc"] = DateTime.UtcNow.AddSeconds(-1);
                if (change == "execution-expired") json["executionDeadlineUtc"] = DateTime.UtcNow.AddSeconds(-1);
                if (change == "stored-long-expiry") json["cloudflareDelegation"]!["expiresUtc"] = DateTime.UtcNow.AddSeconds(121);
                if (change == "stored-past-deadline") json["executionDeadlineUtc"] = DateTime.UtcNow.AddSeconds(30);
                if (change == "stored-wrong-user") json["cloudflareDelegation"]!["userId"] = Guid.NewGuid().ToString();
                if (change == "missing-scope") json["cloudflareDelegation"] = null;
                if (change == "missing-role") json["cloudflareDelegation"]!["roles"] = new JsonArray("Agent");
                if (change == "unknown-token-field") json["cloudflareDelegation"]!["bearerToken"] = "synthetic-never-return";
                message.AiTurnMetadataJson = json.ToJsonString();
                break;
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Null(await fixture.NewService().GetFounderAiOperationDelegationAsync(actor, conversationId, operationId));
        if (change == "scope-expired")
            Assert.True((await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, request.ConversationId, new())).Succeeded);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
    }

    [Fact]
    public async Task FounderAiDelegation_ReplayCannotReplaceSessionRolesOrExtendStoredExpiry()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var scope = DelegationScope();
        var request = fixture.Request("Immutable callback scope.") with { CloudflareDelegation = scope };
        Assert.True((await fixture.Service.BeginFounderAiTurnAsync(request)).Succeeded);
        Assert.Equal("Pending", (await fixture.Service.BeginFounderAiTurnAsync(request)).State);
        foreach (var changed in new[]
        {
            scope with { SessionId = "another-session" }, scope with { Roles = new[] { "Founder" } },
            scope with { AuthorizationVersion = "new-version" }, scope with { ExpiresUtc = scope.ExpiresUtc.AddSeconds(1) }
        })
            Assert.Equal("FOUNDER_HISTORY_REPLAY_MISMATCH", (await fixture.Service.BeginFounderAiTurnAsync(request with { CloudflareDelegation = changed })).ErrorCode);
        var stored = await fixture.Service.GetFounderAiOperationDelegationAsync(fixture.Actor, request.ConversationId, request.OperationId);
        Assert.NotNull(stored);
        Assert.Equal(JsonSerializer.Serialize(scope), JsonSerializer.Serialize(stored.Delegation));
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }
}
