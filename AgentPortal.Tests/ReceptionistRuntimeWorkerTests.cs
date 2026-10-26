using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Data.Configurations;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Runs the shipped hosted worker, SQL authority and application router against a local scripted provider.</summary>
public sealed partial class ReceptionistRuntimeWorkerTests
{
    [Fact]
    public async Task DisabledRuntimeAfterRestartStillStopsAndReconcilesClosureAndFinalUsage()
    {
        await using var f = await Fixture.CreateAsync(enabled: false);
        var callId = await f.ReserveAsync("disabled-restart", accepted: true);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => f.Transport.Hangups.Count == 1);

        var stopped = await f.CallAsync(callId);
        Assert.Equal(stopped.ReservedUtc, stopped.MustStopUtc);
        Assert.True(stopped.SidebandObservationOnly);
        Assert.Null(stopped.ProviderClosedUtc);
        Assert.False(stopped.VoiceUsageFinal);
        Assert.Empty(f.Transport.Accepts);
        Assert.Empty(socket.Sent);
        await using (var db = f.Database())
            Assert.Equal("runtime:runtime_disabled", (await db.Set<ReceptionistActionIntent>()
                .SingleAsync(x => x.CallId == callId && x.CapabilityKey == "__runtime_stop")).ConfirmationReceipt);

        socket.Queue(Closed("disabled-restart", "closed-disabled", 7.25m));
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
        await host.StopAsync();
        await AssertFinalUsageWithMoneyStillReservedAsync(f, callId, 8);
        Assert.Single(f.Transport.Hangups);
        Assert.Empty(socket.Sent);
        f.Transport.AssertBinding("disabled-restart");
    }

    [Fact]
    public async Task UnknownAcceptSurvivesTwoWorkerRestartsWithoutAnotherAcceptAttempt()
    {
        await using var f = await Fixture.CreateAsync();
        var callId = await f.ReserveAsync("uncertain-restart");
        var claim = await f.Admission.ClaimProviderAcceptAsync(callId, "process-that-lost-its-response");
        Assert.True(claim.MayExecute);

        await using (var first = await f.StartAsync())
        {
            var socket = await f.Transport.SocketAsync(0);
            Assert.True((await f.CallAsync(callId)).SidebandObservationOnly);
            Assert.Empty(socket.Sent);
            await first.StopAsync();
        }
        var unknown = await f.CallAsync(callId);
        Assert.Equal("uncertain", unknown.State);
        Assert.Equal(claim.AttemptId, unknown.ProviderAcceptAttemptId);
        Assert.Null(unknown.ProviderClosedUtc);
        Assert.False(unknown.VoiceUsageFinal);

        // A new process may observe after the old SQL lease expires. It still cannot retry the uncertain accept.
        f.Clock.Advance(TimeSpan.FromSeconds(21));
        await using (var second = await f.StartAsync())
        {
            var socket = await f.Transport.SocketAsync(1);
            Assert.Empty(socket.Sent);
            socket.Queue(Closed("uncertain-restart", "closed-uncertain", 6m));
            await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
            await second.StopAsync();
        }
        Assert.Empty(f.Transport.Accepts);
        Assert.Empty(f.Transport.Hangups);
        Assert.Equal(claim.AttemptId, (await f.CallAsync(callId)).ProviderAcceptAttemptId);
        await AssertFinalUsageWithMoneyStillReservedAsync(f, callId, 6);
        f.Transport.AssertBinding("uncertain-restart");
    }

    [Fact]
    public async Task FailedAttachPersistsStopAndRestartDoesNotRepeatAcceptOrUnknownHangup()
    {
        await using var f = await Fixture.CreateAsync();
        f.Transport.FailAttach = true;
        var callId = await f.ReserveAsync("attach-failure");
        await using (var first = await f.StartAsync())
        {
            await EventuallyAsync(() => f.Transport.Hangups.Count == 1);
            await EventuallyAsync(async () =>
            {
                await using var db = f.Database();
                return await db.Set<ReceptionistActionIntent>().AnyAsync(x => x.CallId == callId &&
                    x.CapabilityKey == "__provider_stop" && x.State == "uncertain");
            });
            await first.StopAsync();
        }
        var failed = await f.CallAsync(callId);
        Assert.NotNull(failed.ProviderAcceptAttemptId);
        Assert.Equal("accepted", failed.State);
        Assert.Null(failed.ProviderClosedUtc);
        Assert.False(failed.VoiceUsageFinal);
        await using (var db = f.Database())
        {
            Assert.Equal("runtime:transport_lost", (await db.Set<ReceptionistActionIntent>()
                .SingleAsync(x => x.CallId == callId && x.CapabilityKey == "__runtime_stop")).ConfirmationReceipt);
            var stop = await db.Set<ReceptionistActionIntent>()
                .SingleAsync(x => x.CallId == callId && x.CapabilityKey == "__provider_stop");
            Assert.Equal("uncertain", stop.State);
            Assert.NotEqual(Guid.Empty, stop.AttemptId);
        }

        f.Clock.Advance(TimeSpan.FromSeconds(21));
        await using (var second = await f.StartAsync())
        {
            await EventuallyAsync(() => f.Transport.Attaches.Count == 2);
            await second.StopAsync();
        }
        Assert.Single(f.Transport.Accepts);
        Assert.Single(f.Transport.Hangups);
        Assert.Equal(failed.ProviderAcceptAttemptId, (await f.CallAsync(callId)).ProviderAcceptAttemptId);
        await using (var db = f.Database())
            Assert.Equal(1, await db.Set<ReceptionistActionIntent>()
                .CountAsync(x => x.CallId == callId && x.CapabilityKey == "__provider_stop"));
        f.Transport.AssertBinding("attach-failure");
    }

    [Fact]
    public async Task OwnerKillFencesPreparedInquiryAndAllLaterDialogueOnTheExistingSocket()
    {
        await using var f = await Fixture.CreateAsync();
        var callId = await f.ReserveAsync("owner-kill", accepted: true);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(x => x.GetProperty("type").GetString() == "session.instructions.append"));
        socket.Queue(Input("need", "I need a website to explain my services.", 100, 200));
        socket.Queue(Delegation("prepare-event", "prepare-request"));
        await EventuallyAsync(() => socket.Sent.Any(x => HasDelegation(x, "prepare-request") &&
            x.GetProperty("content").GetString()!.Contains("May I save this request for review?", StringComparison.Ordinal)));
        var summary = socket.Sent.Where(x => HasDelegation(x, "prepare-request")).ToArray();
        Assert.NotEmpty(summary);
        Assert.Contains(summary, x => x.GetProperty("content").GetString()!.Contains("Service: Website services. Request: I need a website to explain my services.", StringComparison.Ordinal));
        var oldLease = (await f.CallAsync(callId)).SidebandLeaseToken!.Value;

        await f.DisableOwnerAsync();
        Assert.False(await f.Admission.ValidateSidebandLeaseAsync(callId, oldLease));
        var sendsBeforeKill = socket.Sent.Count;
        foreach (var item in summary)
            socket.Queue(new
            {
                type = "session.commentary.appended", event_id = "late-ack-" + item.GetProperty("event_id").GetString(),
                client_event_id = item.GetProperty("event_id").GetString(), start_ms = 490, end_ms = 500
            });
        socket.Queue(Input("late-confirmation", "Please save the request you just read back.", 600, 900));
        socket.Queue(Delegation("late-delegation-event", "save-after-kill"));
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        socket.Queue(new { type = "session.usage.updated", event_id = "after-kill-usage", usage = new { seconds = 3 } });
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ObservedVoiceSeconds == 3);
        await EventuallyAsync(() => f.Transport.Hangups.Count == 1);

        Assert.True((await f.CallAsync(callId)).SidebandObservationOnly);
        Assert.Equal(sendsBeforeKill, socket.Sent.Count);
        await using (var db = f.Database())
            Assert.Empty(await db.Set<ReceptionistActionIntent>()
                .Where(x => x.CallId == callId && x.CapabilityKey == ReceptionistCapabilities.CanonicalIntake).ToListAsync());
        socket.Queue(Closed("owner-kill", "closed-after-kill", 3m));
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
        await host.StopAsync();
        Assert.Equal(sendsBeforeKill, socket.Sent.Count);
        Assert.Single(f.Transport.Hangups);
        Assert.Empty(f.Transport.Accepts);
    }

    [Fact]
    public async Task BufferedStartedAndUsageEventsCannotAuthorizeAnotherUnknownHangup()
    {
        await using var f = await Fixture.CreateAsync(enabled: false);
        var callId = await f.ReserveAsync("buffered-stop-events", accepted: true);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => f.Transport.Hangups.Count == 1);
        await EventuallyAsync(async () =>
        {
            await using var db = f.Database();
            return await db.Set<ReceptionistActionIntent>().AnyAsync(x => x.CallId == callId &&
                x.CapabilityKey == "__provider_stop" && x.State == "uncertain");
        });
        // These valid events carry no provider timestamp establishing activity after the existing stop attempt.
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        socket.Queue(new
        {
            type = "session.started", event_id = "buffered-start", session = new { id = "buffered-stop-events", model = "gpt-live-1" }
        });
        socket.Queue(new { type = "session.usage.updated", event_id = "buffered-usage", usage = new { seconds = 4 } });
        socket.Queue(Closed("buffered-stop-events", "closed-buffered", 4m));
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
        await host.StopAsync();

        Assert.Single(f.Transport.Hangups);
        Assert.Empty(f.Transport.Accepts);
        Assert.Empty(socket.Sent);
        await using (var db = f.Database())
            Assert.Equal(1, await db.Set<ReceptionistActionIntent>()
                .CountAsync(x => x.CallId == callId && x.CapabilityKey == "__provider_stop"));
        await AssertFinalUsageWithMoneyStillReservedAsync(f, callId, 4);
    }

    [Fact]
    public async Task PreviouslyAcceptedCallWithUnavailableBackendStillStopsAndObservesFinalUsage()
    {
        // A persisted call may outlive the application version that could interpret its accepted catalog.
        await using var f = await Fixture.CreateAsync(configuration: Configuration() with { ServiceDefinitionsJson = "[]" });
        var callId = await f.ReserveAsync("unavailable-backend", accepted: true);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => f.Transport.Hangups.Count == 1);
        Assert.True((await f.CallAsync(callId)).SidebandObservationOnly);
        Assert.Empty(socket.Sent);
        Assert.Empty(f.Transport.Accepts);
        socket.Queue(Closed("unavailable-backend", "closed-unavailable", 12m));
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
        await host.StopAsync();
        await AssertFinalUsageWithMoneyStillReservedAsync(f, callId, 12);
        Assert.Single(f.Transport.Hangups);
    }

    [Fact]
    public async Task ProjectClaimDriftStopsThePinnedSessionWithoutGrantingFurtherDialogueOrActions()
    {
        await using var f = await Fixture.CreateAsync();
        var callId = await f.ReserveAsync("project-claim-drift", accepted: true);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(x => x.GetProperty("type").GetString() == "session.instructions.append"));
        var oldLease = (await f.CallAsync(callId)).SidebandLeaseToken!.Value;
        var sendsBeforeDrift = socket.Sent.Count;
        await using (var db = f.Database())
            await db.MarketingConnections.Where(x => x.OwnerKey == MarketingOwnerScope.Founder.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReceptionistProviderProjectKey, "inconsistent_project_claim"));

        Assert.Null(await f.Admission.GetIngressBindingAsync("openai", "test_ingress"));
        Assert.False(await f.Admission.ValidateSidebandLeaseAsync(callId, oldLease));
        Assert.Equal("test_project", (await f.Admission.GetCallContextAsync(callId))!.ProviderProjectKey);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        socket.Queue(Input("after-drift-input", "I need a website to explain my services.", 100, 200));
        socket.Queue(Delegation("after-drift-event", "after-drift-delegation"));
        socket.Queue(new { type = "session.usage.updated", event_id = "after-drift-usage", usage = new { seconds = 5 } });
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ObservedVoiceSeconds == 5);
        await EventuallyAsync(() => f.Transport.Hangups.Count == 1);
        Assert.True((await f.CallAsync(callId)).SidebandObservationOnly);
        Assert.Equal(sendsBeforeDrift, socket.Sent.Count);
        await using (var db = f.Database())
            Assert.Empty(await db.Set<ReceptionistActionIntent>().Where(x => x.CallId == callId &&
                x.CapabilityKey == ReceptionistCapabilities.CanonicalIntake).ToListAsync());

        socket.Queue(Closed("project-claim-drift", "closed-original-project", 5m));
        await EventuallyAsync(async () => (await f.CallAsync(callId)).ProviderClosedUtc is not null);
        await host.StopAsync();
        await AssertFinalUsageWithMoneyStillReservedAsync(f, callId, 5);
        Assert.Single(f.Transport.Hangups);
        Assert.Empty(f.Transport.Accepts);
        Assert.Equal(sendsBeforeDrift, socket.Sent.Count);
        f.Transport.AssertBinding("project-claim-drift");
    }

    private static object Closed(string session, string eventId, decimal seconds) => new
    { type = "session.closed", event_id = eventId, session = new { id = session, model = "gpt-live-1" }, usage = new { seconds } };
    private static object Input(string eventId, string text, int start, int end) => new
    { type = "session.input_transcript.delta", event_id = eventId, delta = text, start_ms = start, end_ms = end };
    private static object Delegation(string eventId, string delegationId) => new
    { type = "session.delegation.created", event_id = eventId, delegation = new { id = delegationId, target = "client" } };
    private static bool HasDelegation(JsonElement value, string id) => value.TryGetProperty("delegation_id", out var actual) && actual.GetString() == id;

    private static async Task AssertFinalUsageWithMoneyStillReservedAsync(Fixture fixture, Guid callId, long seconds)
    {
        await using var db = fixture.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == callId);
        Assert.Equal("closed", call.State);
        Assert.NotNull(call.ProviderClosedUtc);
        Assert.True(call.VoiceUsageFinal);
        Assert.False(call.VoiceAllocationUncertain);
        Assert.Equal(seconds, call.ObservedVoiceSeconds);
        Assert.Equal(seconds, (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.Id == call.OwnerBudgetPeriodId)).VoiceSecondsCommitted);
        var cost = await db.Set<ReceptionistCostReservation>().SingleAsync(x => x.CallId == callId);
        Assert.False(cost.CashFinal);
        Assert.False(cost.ConsumptionFinal);
    }

    private static Task EventuallyAsync(Func<bool> predicate) => EventuallyAsync(() => Task.FromResult(predicate()));
    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The expected local worker state was not reached.");
            await Task.Delay(20);
        }
    }

    private static ReceptionistBusinessConfiguration Configuration() => new()
    {
        BusinessName = "Synthetic worker-test owner", ConversationInstructions = "Use the synthetic approved owner policy.",
        BusinessPolicy = "Use only confirmed facts and accepted capabilities.", TimeZoneId = "UTC", Currency = "USD",
        MonthlyCashLimit = 40m, MonthlyConsumptionLimit = 80m, MonthlyVoiceSecondsLimit = 10000,
        MaximumConcurrentCalls = 2, MaximumCallSeconds = 120, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 20,
        Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, TimeOnly.MinValue, new TimeOnly(23, 59, 59))).ToArray(),
        RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
        ServiceDefinitionsJson = "[{\"serviceKey\":\"website_services\",\"displayName\":\"Website services\",\"aliases\":[\"website\"],\"discoveryQuestion\":\"What would you like the website to help with?\",\"publicSummary\":\"Synthetic approved website information.\"}]",
        ApprovedKnowledgeJson = "{\"answers\":[]}",
        IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"website_services\",\"crmBucket\":\"ServiceInquiry\"}],\"notifyOwner\":false}}"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "reception-worker-" + Guid.NewGuid().ToString("N") + ".sqlite");
        private ServiceProvider provider = null!;
        private MarketingCredentialProtector protector = null!;
        private Guid connectionId;
        private Guid configurationId;
        internal DbContextOptions<MasterAppDbContext> Options { get; private set; } = null!;
        internal readonly TestClock Clock = new(new DateTime(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc));
        internal readonly ScriptedProvider Transport = new();
        internal ReceptionistAdmissionService Admission => new(Options, Clock);
        internal MasterAppDbContext Database() => new(Options);
        internal IServiceScope CreateScope() => provider.CreateScope();

        internal static async Task<Fixture> CreateAsync(bool enabled = true, ReceptionistBusinessConfiguration? configuration = null, bool browser = false, IInterceptor? interceptor = null)
        {
            var f = new Fixture();
            var connection = "Data Source=" + f.path + ";Pooling=False;Default Timeout=20";
            await using (var schema = new ReceptionSchemaContext(new DbContextOptionsBuilder().UseSqlite(connection).Options))
            {
                var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).UseModel(schema.Model);
                if (interceptor is not null) options.AddInterceptors(interceptor);
                f.Options = options.Options;
                await schema.Database.EnsureCreatedAsync();
            }
            var owner = MarketingOwnerScope.Founder;
            f.protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
            configuration ??= Configuration();
            var funding = await f.OpenBudgetAsync("funding", "synthetic-shared-provider");
            await using (var db = f.Database())
            {
                var store = new MarketingConnectionStore(db, null!);
                var saved = await store.SaveReceptionistConfigurationAsync(owner, "openai", Guid.Empty, configuration, "test-owner");
                await f.OpenBudgetAsync("owner", owner.Key);
                var binding = new ReceptionistProviderBinding
                {
                    IngressKey = "test_ingress", ProviderProjectKey = "test_project", Model = "gpt-live-1", Voice = "marin",
                    BackendMode = "application_services", Transport = "sip", CredentialReference = "test_api", WebhookSecretReference = "test_webhook",
                    ProtectedApiCredential = ReceptionistCredentialEnvelope.Protect(f.protector, owner, "openai", "test_project", "voice-api", "synthetic-local-test-key"),
                    ProtectedWebhookSecret = ReceptionistCredentialEnvelope.Protect(f.protector, owner, "openai", "test_project", "voice-webhook", "synthetic-local-webhook-key"),
                    ProviderVerificationReceipt = "synthetic-provider-acceptance", CostVerificationReceipt = "synthetic-cost-acceptance",
                    ExclusiveProjectVerificationReceipt = "synthetic-exclusive-owner-project-verification",
                    DisclosureConfigurationReceipt = "synthetic-disclosure", RetentionConfigurationReceipt = "synthetic-retention",
                    CarrierFallbackReceipt = "synthetic-carrier-fallback", MaximumSupportedCallSeconds = configuration.MaximumCallSeconds,
                    Costs = [new("provider_cost", funding, 2m, 0m, 3m, 0m)],
                    Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, ReceptionistInquiryAuthority.RouteKey,
                        ReceptionistCapabilityStates.Accepted, "synthetic-canonical-intake-acceptance")]
                };
                if (browser) binding = binding with
                {
                    Transport = "webrtc", WebhookSecretReference = "", ProtectedWebhookSecret = null,
                    CarrierFallbackReceipt = "", WebsiteFallbackReceipt = "synthetic-website-fallback",
                    ControlledTestOnly = false, PublicActivationReceipt = "synthetic-public-activation"
                };
                saved = await store.AcceptReceptionistBindingAsync(owner, "openai", saved.ConnectionRevision, binding, "test-operator");
                await store.SetReceptionistAdmissionAsync(owner, "openai", saved.ConnectionRevision, true);
            }
            var verified = (await f.Admission.GetIngressBindingAsync("openai", "test_ingress"))!;
            f.connectionId = verified.MarketingConnectionId;
            f.configurationId = verified.ConfigurationId;
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Receptionist:Enabled"] = enabled.ToString()
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(f.Options);
            services.AddSingleton<TimeProvider>(f.Clock);
            services.AddSingleton<IConfiguration>(settings);
            services.AddSingleton(f.protector);
            services.AddScoped<MasterAppDbContext>();
            services.AddScoped<MarketingConnectionStore>();
            services.AddScoped<ReceptionistAdmissionService>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddScoped<Infrastructure.Bookings.IReceptionistApplicationBooking, Infrastructure.Bookings.ReceptionistBookingAuthority>();
            services.AddReceptionistRuntime();
            services.Replace(ServiceDescriptor.Singleton<IOpenAiReceptionistClient>(f.Transport));
            f.provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            return f;
        }

        private async Task<Guid> OpenBudgetAsync(string kind, string scope)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            var id = await Admission.OpenBudgetPeriodAsync(new(kind, scope, "USD", "UTC", now.Year, now.Month,
                40m, 80m, 0m, 0m, 0, "synthetic-budget-opening:" + scope, now.AddTicks(-1)));
            var state = (await Admission.GetBudgetAccountAsync(id))!;
            await Admission.SetBudgetAdmissionAsync(id, state.Revision, true);
            return id;
        }

        internal async Task<Guid> ReserveAsync(string session, bool accepted = false)
        {
            var result = await Admission.ReserveAsync(new("openai", "test_ingress", "test_project", session, "synthetic-event-" + session,
                true, connectionId, configurationId, MarketingOwnerScope.Founder.Key));
            Assert.NotNull(result.CallId);
            var id = result.CallId.Value;
            if (accepted)
            {
                Assert.True((await Admission.ClaimProviderAcceptAsync(id, "previous-process")).MayExecute);
                await Admission.RecordProviderStateAsync(id, "accepted", "synthetic-accepted:" + session);
            }
            return id;
        }

        internal async Task<ReceptionistCall> CallAsync(Guid callId)
        {
            await using var db = Database();
            return await db.Set<ReceptionistCall>().AsNoTracking().SingleAsync(x => x.Id == callId);
        }

        internal async Task DisableOwnerAsync()
        {
            await using var db = Database();
            var store = new MarketingConnectionStore(db, null!);
            var current = await store.GetReceptionistConfigurationAsync(MarketingOwnerScope.Founder, "openai");
            await store.SetReceptionistAdmissionAsync(MarketingOwnerScope.Founder, "openai", current.ConnectionRevision, false);
        }

        internal async Task<WorkerHost> StartAsync()
        {
            var worker = new ReceptionistRuntimeWorker(provider.GetRequiredService<IServiceScopeFactory>(), Clock,
                NullLogger<ReceptionistRuntimeWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            return new(worker);
        }

        public async ValueTask DisposeAsync()
        {
            if (provider is not null) await provider.DisposeAsync();
            protector?.Dispose();
            File.Delete(path);
        }
    }

    private sealed class WorkerHost(ReceptionistRuntimeWorker worker) : IAsyncDisposable
    {
        private bool stopped;
        internal async Task StopAsync()
        {
            if (stopped) return;
            stopped = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(timeout.Token);
            if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
        }
        public async ValueTask DisposeAsync() { try { await StopAsync(); } finally { worker.Dispose(); } }
    }

    private sealed record ProviderRequest(string Project, string Session, string Credential);

    private sealed class ScriptedProvider : IOpenAiReceptionistClient
    {
        internal readonly ConcurrentQueue<ProviderRequest> Accepts = new();
        internal readonly ConcurrentQueue<ProviderRequest> Hangups = new();
        internal readonly ConcurrentQueue<ProviderRequest> Attaches = new();
        private readonly ConcurrentQueue<ScriptedSocket> sockets = new();
        internal bool FailAttach;
        internal int BrowserCreates;
        internal Func<CancellationToken, Task<OpenAiReceptionistBrowserOutcome>>? BrowserCreate;
        public Task<OpenAiReceptionistBrowserOutcome> CreateBrowserAsync(string projectId, string credential,
            OpenAiReceptionistSessionSettings settings, string offer, CancellationToken ct)
        {
            BrowserCreates++;
            return BrowserCreate?.Invoke(ct) ?? Task.FromResult(new OpenAiReceptionistBrowserOutcome(
                true, "live_browser_controller", "v=0\r\n", 201, "synthetic-browser-create"));
        }
        public Task<OpenAiReceptionistHttpOutcome> AcceptAsync(string projectId, string sessionId, string credential,
            OpenAiReceptionistSessionSettings settings, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("gpt-live-1", settings.Model);
            Assert.Equal("application_services", settings.BackendMode);
            Accepts.Enqueue(new(projectId, sessionId, credential));
            return Task.FromResult(new OpenAiReceptionistHttpOutcome(true, 200, "synthetic-accept-response"));
        }
        public Task<OpenAiReceptionistHttpOutcome> HangupAsync(string projectId, string sessionId, string credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Hangups.Enqueue(new(projectId, sessionId, credential));
            return Task.FromResult(new OpenAiReceptionistHttpOutcome(false, null, "synthetic-hangup-response-lost"));
        }
        public Task<WebSocket> AttachAsync(string projectId, string sessionId, string credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Attaches.Enqueue(new(projectId, sessionId, credential));
            if (FailAttach) throw new WebSocketException("Synthetic attach failure.");
            var socket = new ScriptedSocket();
            sockets.Enqueue(socket);
            return Task.FromResult<WebSocket>(socket);
        }
        internal async Task<ScriptedSocket> SocketAsync(int index)
        {
            await EventuallyAsync(() => sockets.Count > index);
            return sockets.ToArray()[index];
        }
        internal void AssertBinding(string session) => Assert.All(Accepts.Concat(Hangups).Concat(Attaches), request =>
        {
            Assert.Equal("test_project", request.Project);
            Assert.Equal(session, request.Session);
            Assert.Equal("synthetic-local-test-key", request.Credential);
        });
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource lifetime = new();
        private int state = (int)WebSocketState.Open;
        private byte[]? frame;
        private int offset;
        internal readonly ConcurrentQueue<JsonElement> Sent = new();
        internal void Queue(object value) => Assert.True(incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value)));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref state);
        public override string? SubProtocol => null;
        public override void Abort() { Interlocked.Exchange(ref state, (int)WebSocketState.Aborted); lifetime.Cancel(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            frame ??= await incoming.Reader.ReadAsync(linked.Token);
            var count = Math.Min(buffer.Length, frame.Length - offset);
            frame.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            var end = offset == frame.Length;
            if (end) { frame = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, end);
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State != WebSocketState.Open) throw new WebSocketException("Synthetic socket is no longer open.");
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            using var document = JsonDocument.Parse(buffer);
            Sent.Enqueue(document.RootElement.Clone());
            return ValueTask.CompletedTask;
        }
        public override void Dispose() { Abort(); }
    }

    // Provider/accounting time and the worker's scheduled delays advance together, without real provider calls or long sleeps.
    private sealed class TestClock(DateTime now) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ScheduledTimer> timers = [];
        private DateTime current = now;
        internal bool HasScheduledTimer { get { lock (gate) return timers.Any(t => !t.Disposed && t.Due is not null); } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return new(current); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new ScheduledTimer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(TimeSpan by)
        {
            List<(TimerCallback Callback, object? State)> ready = [];
            lock (gate)
            {
                current = current.Add(by);
                foreach (var timer in timers.ToArray())
                    if (!timer.Disposed && timer.Due is { } due && due <= current)
                    {
                        timer.Due = timer.Period > TimeSpan.Zero ? current.Add(timer.Period) : null;
                        ready.Add((timer.Callback, timer.State));
                    }
            }
            foreach (var (callback, state) in ready) callback(state);
        }
        private sealed class ScheduledTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal DateTime? Due;
            internal TimeSpan Period;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.current.Add(dueTime);
                    Period = period;
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) { Disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ReceptionSchemaContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new MarketingConnectionConfiguration());
            ReceptionistModelConfiguration.Configure(model, Database.ProviderName);
            foreach (var type in new[] { typeof(AgentTrackingProfile), typeof(CommerceBusiness) })
            {
                var entity = model.Entity(type);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name != "Id")) entity.Ignore(property.Name);
                entity.HasKey("Id");
            }
            model.Entity<WebsiteContentState>().HasKey(x => x.Id);
            model.Entity<WebsiteContentVersion>().HasKey(x => x.Id);
            var allowed = new HashSet<Type>(typeof(ReceptionistCall).Assembly.GetTypes().Where(x =>
                x.Namespace == "Domain.Entities" && x.Name.StartsWith("Receptionist", StringComparison.Ordinal)))
            { typeof(MarketingConnection), typeof(AgentTrackingProfile), typeof(CommerceBusiness), typeof(WebsiteContentState), typeof(WebsiteContentVersion) };
            foreach (var entity in model.Model.GetEntityTypes().ToArray())
                if (!allowed.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
        }
    }
}
