using System;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared.Calling;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class MessagingServiceTests
{
    [Theory]
    [InlineData(1205, true, 1)]
    [InlineData(-2, false, 1)]
    [InlineData(1205, false, 2)]
    public async Task DirectCalls_OfferEpochAndOutboxAreAtomic_OnlyKnownDeadlockRetries(int errorNumber, bool retry, int failures)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var failure = new CallSignalFailure(errorNumber, failures);
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(failure).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid(); var caller = Guid.NewGuid(); var callee = Guid.NewGuid();
        Assert.True((await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default)).Succeeded);
        Assert.True((await service.ExecuteAsync("client-1", "Client", new("accept", callee, id), default)).Succeeded);
        failure.Armed = true;
        var command = new LegendCallCommand("signal", caller, id, SignalKind: "offer", SignalData: "sdp", Epoch: 1);
        if (retry)
            Assert.True((await service.ExecuteAsync("agent-1", "Agent", command, default)).Succeeded);
        else
            await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync("agent-1", "Agent", command, default));
        await using var verification = new MasterAppDbContext(options);
        var saved = await verification.LegendCallSessions.SingleAsync(item => item.Id == id);
        Assert.Equal(retry ? 1 : 0, saved.Epoch);
        Assert.Equal(retry ? 2 : 0, await verification.LegendCallSignals.CountAsync(item => item.CallId == id && item.Payload.Contains("\"SignalKind\":\"offer\"")));
        Assert.Equal(failures, failure.InjectedFailures);
    }

    [Theory]
    [InlineData("{\"screenSharing\":true}", true)]
    [InlineData("{\"screenSharing\":false,\"request\":true}", true)]
    [InlineData("{\"screenSharing\":\"true\"}", false)]
    [InlineData("{\"screenSharing\":true,\"request\":1}", false)]
    [InlineData("{\"screenSharing\":true,\"screenSharing\":false}", false)]
    [InlineData("{\"screenSharing\":true,\"url\":\"private\"}", false)]
    public async Task DirectCalls_MediaStateUsesExistingDeviceEpochAndPayloadValidation(string payload, bool valid)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid(); var caller = Guid.NewGuid(); var callee = Guid.NewGuid();
        await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        await service.ExecuteAsync("client-1", "Client", new("accept", callee, id), default);
        var command = new LegendCallCommand("signal", caller, id, SignalKind: "media-state", SignalData: payload, Epoch: 0);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", command with { DeviceId = Guid.NewGuid() }, default)).Succeeded);
        Assert.False((await service.ExecuteAsync("agent-1", "Agent", command with { Epoch = 1 }, default)).Succeeded);
        Assert.Equal(valid, (await service.ExecuteAsync("agent-1", "Agent", command, default)).Succeeded);
        var signals = await db.LegendCallSignals.Where(item => item.Payload.Contains("\"SignalKind\":\"media-state\"")).ToListAsync();
        Assert.Equal(valid ? 2 : 0, signals.Count);
        if (valid) Assert.All(signals, item => Assert.Contains(callee.ToString(), item.Payload));
    }

    [Fact]
    public async Task DirectCalls_PostCommitSnapshotDeadlockNeverReplaysCommittedOffer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var state = new CallPostCommitFailure();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection)
            .AddInterceptors(new CallCommitObserver(state), new CallPostCommitReadObserver(state)).Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAgentAndClientAsync(db, true, false);
        var service = CreateService(db);
        var conversation = (await service.StartConversationAsync(new StartMessagingConversationCommand(new("agent-1", "Agent"), "client-1", "Client", InitialMessageBody: "Call"))).Conversation!;
        var id = Guid.NewGuid(); var caller = Guid.NewGuid(); var callee = Guid.NewGuid();
        await service.ExecuteAsync("agent-1", "Agent", new("invite", caller, id, conversation.Id), default);
        await service.ExecuteAsync("client-1", "Client", new("accept", callee, id), default);
        state.Enabled = true;
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync("agent-1", "Agent",
            new("signal", caller, id, SignalKind: "offer", SignalData: "sdp", Epoch: 1), default));
        Assert.Equal(1, state.Failures);
        await using var verification = new MasterAppDbContext(options);
        Assert.Equal(1, (await verification.LegendCallSessions.SingleAsync(item => item.Id == id)).Epoch);
        Assert.Equal(2, await verification.LegendCallSignals.CountAsync(item => item.CallId == id && item.Payload.Contains("\"SignalKind\":\"offer\"")));
    }

    private sealed class CallPostCommitFailure
    {
        public bool Enabled;
        public bool ReadArmed;
        public int Failures;
    }
    private sealed class CallCommitObserver(CallPostCommitFailure state) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (state.Enabled) { state.Enabled = false; state.ReadArmed = true; }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CallPostCommitReadObserver(CallPostCommitFailure state) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (state.ReadArmed && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal))
            {
                state.ReadArmed = false; state.Failures++;
                throw CreateCallSqlException(1205);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CallSignalFailure(int number, int maximumFailures) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int InjectedFailures { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && InjectedFailures < maximumFailures && command.CommandText.Contains("INSERT INTO \"LegendCallSignals\"", StringComparison.Ordinal))
            {
                InjectedFailures++;
                throw CreateCallSqlException(number);
            }
            return ValueTask.FromResult(result);
        }
    }

    private static SqlException CreateCallSqlException(int number)
    {
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(item => item.GetParameters().Length >= 8 && item.GetParameters()[0].ParameterType == typeof(int));
        var values = constructor.GetParameters().Select((parameter, index) => index == 0 ? (object)number :
            parameter.ParameterType == typeof(string) ? "test" : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        var error = constructor.Invoke(values);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(method => method.Name == "CreateException" && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        return (SqlException)factory.Invoke(null, [errors, "test"])!;
    }
}
