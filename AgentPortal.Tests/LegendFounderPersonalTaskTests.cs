using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task PersonalTask_PublicConversationRetryAfterLostCommitAcknowledgementNeverCreatesAgain()
    {
        using var environment = new FounderEnvironmentScope();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var fault = new LostPersonalTaskReceiptInterceptor();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(fault).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(ProviderTool("legend_create_personal_task",
            """{"title":"Review supplied intake report","due_at":"2030-07-21T16:15:00Z"}"""),
            ProviderText("The task outcome could not be confirmed."));
        var service = CreateService(db, operations.Object, handler, executionEngine: new ExecutionEngine(db));
        var request = Request("legend", "Create my task Review supplied intake report due July 21 2030 at 16:15 UTC.",
            nativeOnly: true, founderCommandConfirmed: true);
        var operation = Guid.NewGuid();
        var first = await service.ReplyAsync(founder, request, operationId: operation);
        Assert.Equal(1, fault.Faults);
        var inferenceCalls = handler.RequestCount;
        Assert.True(inferenceCalls > 0);
        await using var freshDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        var retry = await CreateService(freshDb, operations.Object, handler, executionEngine: new ExecutionEngine(freshDb))
            .ReplyAsync(founder, request, operationId: operation);
        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(first.Message, retry.Message);
        Assert.Equal(inferenceCalls, handler.RequestCount);
        Assert.Equal(0, handler.ExternalClientCount);
        var task = await freshDb.ActionItems.SingleAsync();
        Assert.Equal(operation, task.Id);
        Assert.Equal(FounderEnvironmentScope.FounderId.ToLowerInvariant(), task.OwnerId);
        Assert.Single(await freshDb.ActionLogs.Where(log => log.ActionId == operation && log.Verb == "created").ToListAsync());
    }

    private sealed class LostPersonalTaskReceiptInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _faulted;
        public int Faults => _faulted;
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ActionItem>().Any(entry => entry.Entity.Source == "LegendFounderAi") &&
                Interlocked.CompareExchange(ref _faulted, 1, 0) == 0)
                throw new System.IO.IOException("Injected loss after actual task and log commit.");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PersonalTask_RequiresConfirmationPersistsInOwnCommandCenterAndReplaysWithoutDuplicate()
    {
        using var environment = new FounderEnvironmentScope();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var legend = new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db));
        var engine = new ExecutionEngine(db);
        LegendFounderToolAuthority Authority() => new(legend, null, executionEngine: engine);
        var id = Guid.NewGuid();
        var call = new FounderAiToolCall("task", "legend_create_personal_task",
            """{"title":"Review intake diagnostics","due_at":"2030-07-21T09:15:00-07:00"}""");
        using var denied = JsonDocument.Parse(await Authority().ExecuteAsync(founder, call, "legend", default));
        Assert.Equal("founder_command_confirmation_required", denied.RootElement.GetProperty("error").GetString());
        Assert.Empty(db.ActionItems);
        call = call with { MutationAuthorization = new FounderAiMutationAuthorization(id.ToString("N")) };
        var firstAuthority = Authority();
        using var first = JsonDocument.Parse(await firstAuthority.ExecuteAsync(founder, call, "legend", default));
        Assert.True(first.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(first.RootElement.GetProperty("calendarEventCreated").GetBoolean());
        Assert.False(first.RootElement.GetProperty("notificationScheduled").GetBoolean());
        var task = await db.ActionItems.SingleAsync();
        Assert.Equal(id, task.Id);
        Assert.Equal(FounderEnvironmentScope.FounderId.ToLowerInvariant(), task.OwnerId);
        Assert.Equal(task.OwnerId, task.CreatedBy);
        Assert.Equal(task.OwnerId, task.EffectiveAgentOid);
        Assert.Equal(ActionSurface.CommandCenter, task.ActionSurface);
        Assert.Equal(new DateTime(2030, 7, 21, 16, 15, 0, DateTimeKind.Utc), task.DueDateUtc);
        using var consumed = JsonDocument.Parse(await firstAuthority.ExecuteAsync(founder, call, "legend", default));
        Assert.Equal("founder_mutation_authorization_replayed", consumed.RootElement.GetProperty("error").GetString());
        // A fresh request scope after an acknowledgement loss must reuse the durable task.
        await using var replayDb = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        var replayAuthority = new LegendFounderToolAuthority(new FounderLegendConnectService(operations.Object,
            new AgentProfileAccessResolver(replayDb)), null, executionEngine: new ExecutionEngine(replayDb));
        using var replay = JsonDocument.Parse(await replayAuthority.ExecuteAsync(founder, call, "legend", default));
        Assert.True(replay.RootElement.GetProperty("reused").GetBoolean());
        Assert.EndsWith("Z", replay.RootElement.GetProperty("dueDateUtc").GetString());
        Assert.Single(db.ActionItems);
        Assert.Single(db.ActionLogs.Where(log => log.ActionId == id && log.Verb == "created"));
        var changed = call with { Arguments = call.Arguments.Replace("Review intake diagnostics", "Different task") };
        using var conflict = JsonDocument.Parse(await Authority().ExecuteAsync(founder, changed, "legend", default));
        Assert.Equal("personal_task_operation_conflict", conflict.RootElement.GetProperty("error").GetString());
        Assert.Equal("Review intake diagnostics", task.Title);
        operations.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersonalTask_OperationCollisionCannotOverwriteForeignOrDelegatedTask(bool delegated)
    {
        using var environment = new FounderEnvironmentScope();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var founder = await AddFounderProfileAsync(db);
        var id = Guid.NewGuid();
        db.ActionItems.Add(new ActionItem { Id = id, Title = "Private existing task", OwnerId = "other-user",
            EffectiveAgentOid = delegated ? FounderEnvironmentScope.FounderId.ToLowerInvariant() : "other-user",
            ActionSurface = ActionSurface.CommandCenter });
        await db.SaveChangesAsync();
        await using var attemptDb = new MasterAppDbContext(options);
        var authority = new LegendFounderToolAuthority(new FounderLegendConnectService(Mock.Of<ILegendConnectOperations>(),
            new AgentProfileAccessResolver(attemptDb)), null, executionEngine: new ExecutionEngine(attemptDb));
        var call = new FounderAiToolCall("task", "legend_create_personal_task",
            """{"title":"New task","due_at":"2030-07-21T16:15:00Z"}""", new(id.ToString("N")));
        if (delegated)
        {
            var receipt = await authority.ExecuteAsync(founder, call, "legend", default);
            Assert.Contains("personal_task_operation_conflict", receipt);
            Assert.DoesNotContain("Private existing task", receipt);
        }
        else
        {
            // The execution authority cannot read the foreign row. Its PK
            // prevents insertion; the conversation boundary reports tool failure.
            await Assert.ThrowsAsync<DbUpdateException>(() => authority.ExecuteAsync(founder, call, "legend", default));
        }
        await using var verifyDb = new MasterAppDbContext(options);
        var retained = await verifyDb.ActionItems.SingleAsync();
        Assert.Equal("Private existing task", retained.Title);
        Assert.Equal("other-user", retained.OwnerId);
        Assert.Empty(await verifyDb.ActionLogs.ToListAsync());
    }

    [Theory]
    [InlineData("2030-07-21T09:15:00")]
    [InlineData("2030-07-21")]
    [InlineData("07/21/2030 09:15:00Z")]
    public async Task PersonalTask_RejectsAmbiguousDueTimeWithoutWriting(string due)
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var authority = new LegendFounderToolAuthority(new FounderLegendConnectService(
            Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(db)), null, executionEngine: new ExecutionEngine(db));
        var call = new FounderAiToolCall("task", "legend_create_personal_task",
            JsonSerializer.Serialize(new { title = "Review", due_at = due }), new(Guid.NewGuid().ToString("N")));
        using var result = JsonDocument.Parse(await authority.ExecuteAsync(founder, call, "legend", default));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(db.ActionItems);
        Assert.Empty(db.ActionLogs);
    }

    [Fact]
    public async Task PersonalTask_ListExcludesOtherUsersAndDoesNotSerializeActorOrRecordFields()
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var actor = FounderEnvironmentScope.FounderId.ToLowerInvariant();
        db.ActionItems.AddRange(new ActionItem { Title = "My review", OwnerId = actor, EffectiveAgentOid = actor,
            ActionSurface = ActionSurface.CommandCenter, SourceRef = "private-source-reference" },
            new ActionItem { Title = "Other private task", OwnerId = "other-user", EffectiveAgentOid = "other-user", ActionSurface = ActionSurface.CommandCenter });
        await db.SaveChangesAsync();
        var authority = new LegendFounderToolAuthority(new FounderLegendConnectService(
            Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(db)), null, executionEngine: new ExecutionEngine(db));
        var output = await authority.ExecuteAsync(founder, new("list", "legend_list_personal_tasks", """{"period":"today"}"""), "legend", default);
        Assert.Contains("My review", output);
        Assert.DoesNotContain("Other private task", output);
        Assert.DoesNotContain("private-source-reference", output);
        Assert.DoesNotContain("ownerId", output);
        Assert.DoesNotContain("relatedEntityId", output);
    }

    [Fact]
    public async Task CapabilityDiscoveryMatchesRequestPublishedToolsAndProviderRestrictions()
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var authority = new LegendFounderToolAuthority(new FounderLegendConnectService(
            Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(db)), null);
        var available = authority.GetAvailableTools(false, Guid.NewGuid().ToString(), LegendConnectExternalProviderPolicy.NativeOnly, false);
        using var receipt = JsonDocument.Parse(await authority.ExecuteAsync(founder, new("caps", "legend_capabilities", "{}"),
            "legend", default, LegendConnectExternalProviderPolicy.NativeOnly, available));
        var names = receipt.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        var published = available.Select(item => JsonSerializer.SerializeToElement(item).GetProperty("name").GetString()).ToArray();
        Assert.Equal(published, names);
        Assert.DoesNotContain("legend_request_teacher_escalation", names);
        Assert.DoesNotContain("legend_research_internet", names);
        Assert.DoesNotContain("legend_create_personal_task", names);
        Assert.DoesNotContain("legend_list_personal_tasks", names); // unregistered execution engine
        Assert.Contains("legend_remember_conversation_facts", names);
    }
}
