using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderHistory_UsesCanonicalStoreAndPreservesActualProvenanceAcrossServiceInstances(bool relational)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(relational);
        var request = fixture.Request("Compare the two renewal counts without choosing an unsupported number.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(request);
        Assert.True(started.Succeeded, started.ErrorMessage);
        Assert.Equal("Started", started.State);
        var provenance = new MessagingFounderAiResponseProvenance(true, "legend", Stage: "response_partial",
            Reason: "provider_output_incomplete", ResponseAuthority: "LocalFoundation",
            FoundationModel: "controlled-fixture", FoundationHosting: "LegendControlled",
            ExternalAnsweringUsed: false, EscalationUsed: false, ResearchState: "not_needed",
            LearningState: "not_queued", ModelProvenance: "Pretrained");
        var completed = await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, request.ConversationId,
            request.OperationId, started.UserMessage!.Id, "The supplied counts conflict; neither is independently verified.",
            MessagingAuthorKinds.Assistant, provenance));
        Assert.True(completed.Succeeded, completed.ErrorMessage);
        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.NewService().GetFounderAiConversationPageAsync(fixture.Actor,
            request.ConversationId, new());
        Assert.True(reloaded.Succeeded, reloaded.ErrorMessage);
        Assert.Equal(2, reloaded.Conversation!.Messages.Count);
        var assistant = reloaded.Conversation.Messages[1];
        Assert.Equal(provenance, assistant.ResponseProvenance);
        Assert.Equal(MessagingAuthorKinds.Assistant, assistant.AuthorKind);
        Assert.Equal("", assistant.SenderUserId);
        Assert.Equal("", assistant.SenderType);
        Assert.Null(reloaded.Conversation.Messages[0].ResponseProvenance);
        var wire = JsonSerializer.Serialize(reloaded);
        Assert.DoesNotContain("RequestFingerprint", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutionDeadlineUtc", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(request.RequestFingerprint, wire);
        Assert.Equal(0, fixture.Translation.DetectionCallCount);
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
        Assert.Single(await fixture.Db.MessageConversations.ToListAsync());
        Assert.Single(await fixture.Db.MessageConversationParticipants.ToListAsync());
        Assert.Equal(2, await fixture.Db.InternalMessages.CountAsync());
        Assert.Empty(await fixture.Db.LegendFounderAiDiscourseConversations.ToListAsync());
    }

    [Fact]
    public async Task FounderHistory_ReplayRoundTripsFailureWorkResearchAndScheduleMetadata()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var command = fixture.Request("Verify the external source and retain the unresolved outcome.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        var now = DateTime.UtcNow;
        var requestId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        var origin = LegendConnectResearchEvidenceOrigin.UnresolvedEvidence;
        var research = new LegendConnectResearchOutcome(LegendConnectResearchOutcomeState.Failure, origin,
            new(true, LegendConnectResearchNeed.ExplicitVerificationRequest, "explicit_verification",
                LegendConnectResearchAccessClass.PublicReadOnly, "ht", false, false, false, null, now),
            new(sessionId, requestId, now, now, [], [], [], [], [], [], [], 23, null, "failed", "search_unavailable"),
            null, null, null, new("search_unavailable", "The source could not be verified.", false),
            new(requestId, sessionId, "explicit_verification", "ht", new string('c', 64), now, origin,
                null, 0, "blocked-provider", null, "isolated-fixture", [], [], [], [], [], [], now, now,
                23, null, "not_charged", "fixture_authorized_read", null, true, true, "fixture_receipt"));
        // A legitimate multilingual research receipt exceeds the former 64KiB cap.
        var sourceText = new string('界', LegendConnectResearchContracts.MaximumDocumentCharacters);
        var documents = Enumerable.Range(0, LegendConnectResearchContracts.MaximumDocuments)
            .Select(index => new LegendConnectRetrievedDocument($"document-{index}", $"source-{index}",
                $"https://example.org/{index}", sourceText, new string('d', 64), now, true, null, "zh")).ToArray();
        var claims = Enumerable.Range(0, LegendConnectResearchContracts.MaximumClaims)
            .Select(index => new LegendConnectClaimEvidence($"evidence-{index}", $"claim-{index}",
                sourceText[..800], $"source-{index % documents.Length}", $"document-{index % documents.Length}",
                $"citation-{index}", now, SupportingExcerpt: sourceText[..800], EvidenceLanguageCode: "zh")).ToArray();
        research = research with { Session = research.Session with { Documents = documents, ClaimEvidence = claims } };
        var certificate = new LegendConnectGovernedScheduleCertificateSnapshot("signature", "operator", "dimension",
            "schedule", [new(1, 2, 0, 5, 1, 1)], new Dictionary<string, string> { ["premise"] = "supplied" },
            new Dictionary<string, string> { ["conclusion"] = "bounded" }, ["evidence"], 1, "fixture_only");
        var metadata = new MessagingFounderAiResponseProvenance(false, "legend", Stage: "research_failed",
            Reason: "search_unavailable", ResponseAuthority: "SystemDiagnostic", ResearchState: "failed",
            FailureKind: "provider_unavailable", ProviderStatusCode: 503, Reference: "private-reference",
            CompletedWork: ["Approved facts read"], RemainingWork: ["External evidence unavailable"], Resumable: false,
            ModelAssistanceReason: "not_applied", EvidenceOrigin: origin, ResearchOutcome: research,
            ScheduleCertificates: [certificate], ReasoningTransitionPath: ["observed", "unresolved"], BodyIsError: true);
        Assert.True(JsonSerializer.Serialize(metadata).Length > 65_536);
        var originalBody = new string('語', 1_000_000);
        var completed = await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage!.Id, originalBody, MessagingAuthorKinds.Service, metadata));
        Assert.True(completed.Succeeded, completed.ErrorMessage);
        var replay = await fixture.Service.BeginFounderAiTurnAsync(command);
        Assert.Equal("Completed", replay.State);
        Assert.False(replay.TerminalMessage!.ResponseProvenance!.Succeeded);
        Assert.True(replay.TerminalMessage.ResponseProvenance.BodyIsError);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(metadata),
            JsonSerializer.SerializeToNode(replay.TerminalMessage.ResponseProvenance)));
        Assert.Equal(originalBody, replay.TerminalMessage.Body);
        var stored = await fixture.Db.InternalMessages.SingleAsync(item => item.Id == replay.TerminalMessage.Id);
        Assert.InRange(stored.AiTurnMetadataJson!.Length, 65_537, 1_000_000);
        var page = await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId, new());
        Assert.True(page.Succeeded, page.ErrorMessage);
        var reloaded = page.Conversation!.Messages.Single(item => item.Id == replay.TerminalMessage.Id);
        Assert.Equal(originalBody, reloaded.Body);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(metadata),
            JsonSerializer.SerializeToNode(reloaded.ResponseProvenance)));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page).Length, 1, 32_000_000);
        Assert.Equal(2, await fixture.Db.InternalMessages.CountAsync());
    }

    [Fact]
    public async Task FounderHistory_DeniesGenericMessagingAndForeignOrAlternateRoleActors()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var request = fixture.Request("Private organization question.");
        Assert.True((await fixture.Service.BeginFounderAiTurnAsync(request)).Succeeded);
        Assert.False((await fixture.Service.GetConversationAsync(fixture.Actor, request.ConversationId)).Succeeded);
        Assert.DoesNotContain((await fixture.Service.ListConversationsAsync(fixture.Actor, new())).Conversations,
            item => item.Id == request.ConversationId);
        Assert.False((await fixture.Service.SendMessageAsync(new(fixture.Actor, request.ConversationId,
            "Injected assistant answer", Guid.NewGuid().ToString()))).Succeeded);
        foreach (var actor in new[] { new MessagingActor(Guid.NewGuid().ToString(), "Agent"),
            new MessagingActor(fixture.Actor.UserId, "Client"), new MessagingActor("legend-ai", "Assistant") })
        {
            Assert.False((await fixture.Service.GetFounderAiConversationPageAsync(actor, request.ConversationId, new())).Succeeded);
            Assert.False((await fixture.Service.BeginFounderAiTurnAsync(request with { Actor = actor })).Succeeded);
        }
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        // Even a wrongly added member must not turn this protected history into
        // a normal group or grant the original owner access through membership.
        fixture.Db.MessageConversationParticipants.Add(new()
        {
            Id = Guid.NewGuid(), ConversationId = request.ConversationId, UserId = "another-agent",
            ParticipantType = "Agent", IsActive = true, JoinedUtc = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();
        Assert.False((await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, request.ConversationId, new())).Succeeded);
    }

    [Fact]
    public async Task FounderHistory_UnspecifiedStorageTimesRoundTripUtcCursorWithoutLosingTicks()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var command = fixture.Request("Keep the original ordering.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        var completed = await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage!.Id, "A recorded response.", MessagingAuthorKinds.Assistant,
            new(true, "legend")));
        var timestamp = new DateTime(2026, 9, 13, 17, 18, 19, DateTimeKind.Unspecified).AddTicks(1234);
        var stored = await fixture.Db.InternalMessages.OrderBy(item => item.SentUtc).ToArrayAsync();
        stored[0].SentUtc = timestamp;
        stored[1].SentUtc = timestamp.AddTicks(1);
        var thread = await fixture.Db.MessageConversations.SingleAsync();
        thread.CreatedUtc = timestamp; thread.LastMessageUtc = timestamp.AddTicks(1);
        await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        var newest = (await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId,
            new(Take: 1))).Conversation!;
        var last = Assert.Single(newest.Messages);
        Assert.Equal(DateTimeKind.Utc, last.SentUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, newest.CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, newest.LastMessageUtc!.Value.Kind);
        Assert.Equal(timestamp.AddTicks(1).Ticks, last.SentUtc.Ticks);
        var wire = JsonSerializer.Serialize(last.SentUtc);
        Assert.EndsWith("Z\"", wire);
        var cursor = JsonSerializer.Deserialize<DateTime>(wire);
        var older = (await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId,
            new(cursor, 1, BeforeMessageId: last.Id))).Conversation!;
        Assert.Equal(started.UserMessage.Id, Assert.Single(older.Messages).Id);
        Assert.Equal(timestamp.Ticks, older.Messages[0].SentUtc.Ticks);
        Assert.Equal(completed.Message!.Id, last.Id);
    }

    [Fact]
    public async Task FounderHistory_AssistantTypeRemainsOutsideLegacyKnownTypeUnionAndDirectGroupActions()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var command = fixture.Request("An account-owned assistant conversation.");
        Assert.True((await fixture.Service.BeginFounderAiTurnAsync(command)).Succeeded);
        var stored = await fixture.Db.MessageConversations.SingleAsync();
        Assert.Equal(MessagingConversationTypes.Assistant, stored.ConversationType);
        Assert.Equal(MessagingConversationPurposes.FounderAI, stored.Purpose);
        // Conservative superset of every conversation type admitted by the
        // unchanged production 144567d7 authorization union. Additional old
        // membership/profile predicates only narrow this set further.
        var oldTypes = new[] { "Group", "AgentDirect", "ClientAgent", "ClientJourney" };
        Assert.Empty(await fixture.Db.MessageConversations.Where(item => oldTypes.Contains(item.ConversationType))
            .Select(item => item.Id).ToListAsync());
        Assert.False((await fixture.Service.GetConversationAsync(fixture.Actor, stored.Id)).Succeeded);
        Assert.False((await fixture.Service.SetGroupPromotionAsync(new(fixture.Actor, stored.Id, true))).Succeeded);
        Assert.False((await fixture.Service.DeleteGroupAsync(new(fixture.Actor, stored.Id))).Succeeded);
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.Single(await fixture.Db.MessageConversationParticipants.ToListAsync());
    }

    [Fact]
    public async Task FounderHistory_OperationReplayPinsPayloadPolicyAndDeadlineWithoutDuplicatingTurns()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var command = fixture.Request("Retain the verified preference only.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        var pending = await fixture.Service.BeginFounderAiTurnAsync(command with { ExecutionDeadlineUtc = DateTime.UtcNow.AddHours(1) });
        Assert.Equal("Pending", pending.State);
        Assert.Equal(started.UserMessage!.Id, pending.UserMessage!.Id);
        var stored = Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
        Assert.Equal(command.ExecutionDeadlineUtc, JsonDocument.Parse(stored.AiTurnMetadataJson!).RootElement
            .GetProperty("executionDeadlineUtc").GetDateTime());
        Assert.Equal("FOUNDER_HISTORY_REPLAY_MISMATCH", (await fixture.Service.BeginFounderAiTurnAsync(command with
        { RequestFingerprint = new string('b', 64) })).ErrorCode);
        Assert.Equal("FOUNDER_HISTORY_REPLAY_MISMATCH", (await fixture.Service.BeginFounderAiTurnAsync(command with
        { Body = "Different action" })).ErrorCode);
        var complete = new MessagingFounderAiCompleteTurnCommand(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage.Id, "The preference was retained as a submitted correction.",
            MessagingAuthorKinds.Assistant, new(true, "legend", LearningState: "submitted"));
        Assert.True((await fixture.Service.CompleteFounderAiTurnAsync(complete)).Succeeded);
        Assert.True((await fixture.Service.CompleteFounderAiTurnAsync(complete)).Succeeded);
        Assert.Equal("Completed", (await fixture.Service.BeginFounderAiTurnAsync(command)).State);
        Assert.Equal(2, await fixture.Db.InternalMessages.CountAsync());
        Assert.False((await fixture.Service.CompleteFounderAiTurnAsync(complete with
        { Provenance = complete.Provenance with { LearningState = "promoted" } })).Succeeded);
    }

    [Fact]
    public async Task FounderHistory_RequiresCurrentCursorAndRejectsSimultaneousDifferentOperation()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var command = fixture.Request("First question.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        Assert.Equal("FOUNDER_HISTORY_STALE", (await fixture.Service.BeginFounderAiTurnAsync(command with
        { OperationId = Guid.NewGuid() })).ErrorCode);
        Assert.Equal("FOUNDER_HISTORY_PENDING", (await fixture.Service.BeginFounderAiTurnAsync(command with
        { OperationId = Guid.NewGuid(), ExpectedLastMessageId = started.LastMessageId })).ErrorCode);
        Assert.Single(await fixture.Db.InternalMessages.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderHistory_ExpiredOperationBecomesUnknownAndNeverAcceptsLateCompletion(bool newOperationFirst)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var command = fixture.Request("A consequential action whose final receipt could be lost.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        await fixture.ExpireAsync(started.UserMessage!.Id);
        var fresh = command with { OperationId = Guid.NewGuid(), ExpectedLastMessageId = started.LastMessageId,
            Body = "I have checked the earlier action. Continue with this new request." };
        if (newOperationFirst)
            Assert.Equal("Started", (await fixture.Service.BeginFounderAiTurnAsync(fresh)).State);
        var replay = await fixture.Service.BeginFounderAiTurnAsync(command);
        Assert.Equal("OutcomeUnknown", replay.State);
        Assert.False(replay.TerminalMessage!.ResponseProvenance!.Succeeded);
        Assert.Equal("outcome_unknown", replay.TerminalMessage.ResponseProvenance.Reason);
        Assert.Equal(MessagingFounderAiResponseProvenance.OutcomeUnknown(command.Mode), replay.TerminalMessage.ResponseProvenance);
        Assert.Equal(MessagingAuthorKinds.Service, replay.TerminalMessage.AuthorKind);
        var history = (await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId, new())).Conversation!;
        Assert.Equal(replay.TerminalMessage.ResponseProvenance,
            history.Messages.Single(message => message.Id == replay.TerminalMessage.Id).ResponseProvenance);
        Assert.False((await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage.Id, "Claiming a late success", MessagingAuthorKinds.Assistant,
            new(true, "legend")))).Succeeded);
        Assert.Single(await fixture.Db.InternalMessages.Where(item => item.ReplyToMessageId == started.UserMessage.Id).ToListAsync());
        if (!newOperationFirst)
            Assert.Equal("Started", (await fixture.Service.BeginFounderAiTurnAsync(fresh with
            { ExpectedLastMessageId = replay.LastMessageId })).State);
    }

    [Fact]
    public async Task FounderHistory_ConcurrentExpiryFencesLateCompletionAndRollsBackItsMessage_Sqlite()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var command = fixture.Request("Inspect the operational result without repeating its action.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        var pause = new FounderHistoryTerminalPause();
        await using var completingDb = fixture.SiblingContext(pause);
        var completingService = CreateService(completingDb, configuredFounderOid: FounderTestObjectId,
            configuration: FounderConfiguration(FounderTestObjectId));
        var completion = completingService.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage!.Id, "A late result prepared before expiry.",
            MessagingAuthorKinds.Assistant, new(true, "legend")));
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await fixture.ExpireAsync(started.UserMessage.Id);
            await using var recoveryDb = fixture.SiblingContext();
            var recovery = CreateService(recoveryDb, configuredFounderOid: FounderTestObjectId,
                configuration: FounderConfiguration(FounderTestObjectId));
            Assert.Equal("OutcomeUnknown", (await recovery.BeginFounderAiTurnAsync(command)).State);
        }
        finally { pause.Release.TrySetResult(); }
        var late = await completion;
        Assert.False(late.Succeeded);
        Assert.Equal("FOUNDER_HISTORY_CONFLICT", late.ErrorCode);
        fixture.Db.ChangeTracker.Clear();
        var terminal = Assert.Single(await fixture.Db.InternalMessages.Where(item =>
            item.ReplyToMessageId == started.UserMessage.Id).ToListAsync());
        Assert.Equal(MessagingAuthorKinds.Service, terminal.AuthorKind);
        Assert.DoesNotContain(await fixture.Db.InternalMessages.ToListAsync(), item =>
            item.AuthorKind == MessagingAuthorKinds.Assistant);
    }

    [Fact]
    public async Task FounderHistory_InvalidDeadlineCannotLeaveAnAddedConversationForLaterSave()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var result = await fixture.Service.BeginFounderAiTurnAsync(fixture.Request("A valid body.") with
        { ExecutionDeadlineUtc = DateTime.UtcNow.AddHours(2) });
        Assert.False(result.Succeeded);
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Db.MessageConversations.ToListAsync());
    }

    [Fact]
    public async Task FounderHistory_PreservesLargeBodiesAndBoundsPageBeforeHydration()
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var command = fixture.Request(new string('x', 800_000));
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        Assert.True(started.Succeeded, started.ErrorMessage);
        var complete = await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage!.Id, new string('y', 800_000), MessagingAuthorKinds.Assistant,
            new(true, "legend")));
        Assert.True(complete.Succeeded, complete.ErrorMessage);
        // Four large rows exceed the 32MB conservative page bound; three fit.
        var next = command with { OperationId = Guid.NewGuid(), ExpectedLastMessageId = complete.Message!.Id,
            Body = new string('z', 800_000), RequestFingerprint = new string('e', 64) };
        var nextStarted = await fixture.Service.BeginFounderAiTurnAsync(next);
        Assert.True(nextStarted.Succeeded, nextStarted.ErrorMessage);
        Assert.True((await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            next.OperationId, nextStarted.UserMessage!.Id, new string('w', 800_000), MessagingAuthorKinds.Assistant,
            new(true, "legend")))).Succeeded);
        var newest = (await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId, new())).Conversation!;
        Assert.Equal(3, newest.Messages.Count);
        Assert.All(newest.Messages, message => Assert.Equal(800_000, message.Body.Length));
        Assert.Equal(new[] { 'y', 'z', 'w' }, newest.Messages.Select(message => message.Body[0]));
        Assert.True(newest.HasOlderMessages);
        var cursor = newest.Messages[0];
        var older = (await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId,
            new(cursor.SentUtc, BeforeMessageId: cursor.Id))).Conversation!;
        Assert.Equal(command.Body, Assert.Single(older.Messages).Body);
        Assert.False(older.HasOlderMessages);
        // The general messaging API keeps its own unchanged lower bound.
        Assert.Equal("MESSAGING_MESSAGE_INVALID", (await fixture.Service.SendMessageAsync(new(fixture.Actor,
            command.ConversationId, new string('z', 10001)))).ErrorCode);
    }

    [Theory]
    [InlineData("trailing_spaces")]
    [InlineData("surrogates")]
    [InlineData("escaped_controls")]
    public async Task FounderHistory_PageBoundsAccountForTrailingSpacesUnicodeAndJsonEscaping(string shape)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync(true);
        var text = shape switch
        {
            "trailing_spaces" => "x" + new string(' ', 799_999),
            "surrogates" => string.Concat(Enumerable.Repeat("\U0001F600", 400_000)),
            _ => "x" + new string('\t', 799_999)
        };
        var command = fixture.Request(text);
        var started = await fixture.Service.BeginFounderAiTurnAsync(command);
        Assert.True(started.Succeeded, started.ErrorMessage);
        Assert.True((await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, command.ConversationId,
            command.OperationId, started.UserMessage!.Id, text, MessagingAuthorKinds.Assistant, new(true, "legend")))).Succeeded);
        var page = await fixture.Service.GetFounderAiConversationPageAsync(fixture.Actor, command.ConversationId, new());
        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.All(page.Conversation!.Messages, message => Assert.Equal(text, message.Body));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page).Length, 1, 32_000_000);
        Assert.Equal(0, fixture.Translation.TranslationCallCount);
    }

    [Theory]
    [InlineData("{\"mode\":\"legend\",\"version\":1}")]
    [InlineData("{\"mode\":\"legend\",\"version\":1,\"succeeded\":true,\"inventedPromotion\":true}")]
    public async Task FounderHistory_MissingOrUnknownResponseProvenanceFailsClosed(string corruptReceipt)
    {
        await using var fixture = await FounderHistoryFixture.CreateAsync();
        var request = fixture.Request("Explain this supplied statement.");
        var started = await fixture.Service.BeginFounderAiTurnAsync(request);
        var completed = await fixture.Service.CompleteFounderAiTurnAsync(new(fixture.Actor, request.ConversationId,
            request.OperationId, started.UserMessage!.Id, "An actual answer", MessagingAuthorKinds.Assistant, new(true, "legend")));
        var entity = await fixture.Db.InternalMessages.SingleAsync(item => item.Id == completed.Message!.Id);
        entity.AiTurnMetadataJson = corruptReceipt;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("FOUNDER_HISTORY_INVALID", (await fixture.Service.GetFounderAiConversationPageAsync(
            fixture.Actor, request.ConversationId, new())).ErrorCode);
    }

    private sealed class FounderHistoryFixture : IAsyncDisposable
    {
        private readonly string? _oldFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        private readonly SqliteConnection? _connection;
        public MasterAppDbContext Db { get; }
        public MessagingActor Actor { get; } = new(FounderTestObjectId, MessagingParticipantTypes.Agent);
        public TestTranslationService Translation { get; } = new();
        public MessagingService Service { get; }
        private FounderHistoryFixture(MasterAppDbContext db, SqliteConnection? connection)
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderTestObjectId);
            Db = db; _connection = connection; Service = NewService();
        }
        public MessagingService NewService() => CreateService(Db, Translation, FounderTestObjectId,
            FounderConfiguration(FounderTestObjectId));
        public MasterAppDbContext SiblingContext(SaveChangesInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(_connection!);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options);
        }
        public MessagingFounderAiBeginTurnCommand Request(string body) => new(Actor, Guid.NewGuid(), Guid.NewGuid(),
            null, body, "legend", new string('a', 64), DateTime.UtcNow.AddMinutes(5));
        public static async Task<FounderHistoryFixture> CreateAsync(bool relational = false)
        {
            SqliteConnection? connection = null;
            MasterAppDbContext db;
            if (relational)
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                db = new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
                await db.Database.EnsureCreatedAsync();
            }
            else db = ControllerTestHelpers.BuildDb();
            var fixture = new FounderHistoryFixture(db, connection);
            db.AgentProfiles.Add(new() { Id = Guid.NewGuid(), AgentUserId = FounderTestObjectId,
                AgentUpn = "founder@example.test", FullName = "Founder", IsActive = true });
            await db.SaveChangesAsync();
            return fixture;
        }
        public async Task ExpireAsync(Guid userMessageId)
        {
            var message = await Db.InternalMessages.SingleAsync(item => item.Id == userMessageId);
            var json = JsonNode.Parse(message.AiTurnMetadataJson!)!;
            json["executionDeadlineUtc"] = DateTime.UtcNow.AddSeconds(-1);
            message.AiTurnMetadataJson = json.ToJsonString();
            await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            Environment.SetEnvironmentVariable("FOUNDER_OID", _oldFounder);
        }
    }

    private sealed class FounderHistoryTerminalPause : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<InternalMessage>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.AuthorKind == MessagingAuthorKinds.Assistant))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
