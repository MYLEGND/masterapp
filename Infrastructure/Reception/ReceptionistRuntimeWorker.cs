using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Reception;

/// <summary>The control-plane host runs durable admission work and a fenced sideband. Local tasks are scheduling hints only.</summary>
internal sealed class ReceptionistRuntimeWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ReceptionistRuntimeWorker> logger) : BackgroundService
{
    private readonly string _workerId = "reception-" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, Task> _sessions = [];
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FinalObservationWindow = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in _sessions.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray())
                {
                    await _sessions[completed];
                    _sessions.Remove(completed);
                }
                try
                {
                    using var scope = scopes.CreateScope();
                    // Admission can be disabled while calls from a previous process still require closure and accounting.
                    {
                        var enabled = scope.ServiceProvider.GetRequiredService<ReceptionistRuntimeAuthority>().Enabled;
                        var admission = scope.ServiceProvider.GetRequiredService<ReceptionistAdmissionService>();
                        var stops = await admission.FindStopRequestsAsync(1000, stoppingToken);
                        // Watchdog effects have their own SQL claim and do not wait for a busy conversation slot.
                        await Parallel.ForEachAsync(stops, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = stoppingToken },
                            async (stop, ct) => await StopAsync(stop.CallId, null, ct));
                        var pending = await admission.FindPendingProviderAcceptsAsync(1000, stoppingToken);
                        var uncertain = await admission.FindUncertainProviderCallsAsync(1000, stoppingToken);
                        var active = await admission.FindSidebandCandidatesAsync(1000, stoppingToken);
                        var observations = await admission.FindObservationCandidatesAsync(1000, FinalObservationWindow, stoppingToken);
                        var candidates = observations.Concat(uncertain).Concat(active).Concat(pending)
                            .GroupBy(x => x.CallId).Select(x => x.First()).ToArray();
                        if (!enabled)
                            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = stoppingToken },
                                async (work, ct) =>
                                {
                                    await admission.RequestRuntimeStopAsync(work.CallId, "runtime_disabled", ct);
                                    await StopAsync(work.CallId, null, ct);
                                });
                        foreach (var work in candidates)
                        {
                            if (_sessions.Count >= 64) break;
                            if (_sessions.ContainsKey(work.CallId)) continue;
                            _sessions[work.CallId] = RunCallAsync(work.CallId, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Provider/caller bodies, secrets and exception messages never enter operational logs.
                    logger.LogWarning("Receptionist work remains pending after a control-plane failure.");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(_sessions.Values); }
    }

    private async Task RunCallAsync(Guid callId, CancellationToken hostToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var admission = services.GetRequiredService<ReceptionistAdmissionService>();
            var context = await admission.GetCallContextAsync(callId, hostToken);
            if (context is null || context.State is "closed" or "rejected" ||
                clock.GetUtcNow().UtcDateTime > context.MustStopUtc + FinalObservationWindow) return;
            var runtime = services.GetRequiredService<ReceptionistRuntimeAuthority>();
            var client = services.GetRequiredService<IOpenAiReceptionistClient>();
            string credential;
            var settings = new OpenAiReceptionistSessionSettings(context.Binding.Model, context.Binding.Voice,
                context.Configuration.ConversationInstructions, context.Binding.BackendMode);
            try
            {
                // A broken business configuration must not prevent the accepted transport from observing closure.
                if (context.Provider != Shared.Analytics.MarketingDestinationKeys.OpenAi || context.Binding.Transport != "sip" ||
                    context.ProviderProjectKey != context.Binding.ProviderProjectKey ||
                    !OpenAiReceptionistProtocol.Token(context.ProviderProjectKey, 200) ||
                    !OpenAiReceptionistProtocol.Token(context.ProviderSessionId, 200))
                    throw new InvalidOperationException("The recorded provider transport is unsupported.");
                credential = runtime.Credential(context.Owner, context.Binding, webhook: false);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                await admission.RequestRuntimeStopAsync(callId, "backend_unavailable", hostToken);
                return;
            }
            var businessReady = true;
            try { settings = ReceptionistRuntimeAuthority.Ready(context.Provider, context.Configuration, context.Binding, context.ControlledTest); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
            {
                businessReady = false;
                await admission.RequestRuntimeStopAsync(callId, "backend_unavailable", hostToken);
                await StopAsync(callId, null, hostToken);
                context = await admission.GetCallContextAsync(callId, hostToken) ?? context;
            }
            if (!runtime.Enabled)
            {
                await admission.RequestRuntimeStopAsync(callId, "runtime_disabled", hostToken);
                await StopAsync(callId, null, hostToken);
                context = await admission.GetCallContextAsync(callId, hostToken) ?? context;
            }
            if (runtime.Enabled && businessReady && context.State == "reserved")
            {
                var claim = await admission.ClaimProviderAcceptAsync(callId, _workerId, hostToken);
                if (claim.MayExecute)
                {
                    var result = await client.AcceptAsync(context.ProviderProjectKey, context.ProviderSessionId, credential, settings, hostToken);
                    await admission.RecordProviderStateAsync(callId, result.Confirmed ? "accepted" : "uncertain", result.Receipt, ct: hostToken);
                }
                context = await admission.GetCallContextAsync(callId, hostToken) ?? context;
            }
            var lease = runtime.Enabled && businessReady && context.State == "accepted"
                ? await admission.AcquireSidebandLeaseAsync(callId, _workerId, LeaseDuration, hostToken) : null;
            lease ??= await admission.AcquireObservationLeaseAsync(callId, _workerId, LeaseDuration, hostToken);
            if (lease is null) return;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            var observedUntil = context.MustStopUtc + FinalObservationWindow;
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            WebSocket attached;
            try { attached = await client.AttachAsync(context.ProviderProjectKey, context.ProviderSessionId, credential, connect.Token); }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
            {
                if (!hostToken.IsCancellationRequested)
                {
                    await admission.RequestRuntimeStopAsync(callId, "transport_lost", hostToken);
                    await StopAsync(callId, null, hostToken);
                }
                return;
            }
            using var socket = attached;
            using var sends = new SemaphoreSlim(1, 1);
            var state = new SessionState(context, lease, observedUntil);
            var backendTasks = new List<Task>();
            var heartbeat = HeartbeatAsync(state, admission, runtime, lifetime);
            try
            {
                if (!state.Lease.ObservationOnly)
                    backendTasks.Add(StartApplicationAsync(state, services, socket, sends, admission, lifetime.Token));
                while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var message = await ReceptionistSidebandProtocol.ReceiveAsync(socket, lifetime.Token);
                    if (message is null) break;
                    var data = message.RootElement;
                    if (data.ValueKind != JsonValueKind.Object || !OpenAiReceptionistProtocol.String(data, "type", out var type) ||
                        !OpenAiReceptionistProtocol.String(data, "event_id", out var eventId) || !OpenAiReceptionistProtocol.Token(eventId, 200))
                        continue;
                    var currentLease = state.Lease;
                    var owns = currentLease.ObservationOnly
                        ? await admission.ValidateObservationLeaseAsync(callId, currentLease.Token, lifetime.Token)
                        : await admission.ValidateSidebandLeaseAsync(callId, currentLease.Token, lifetime.Token);
                    if (!owns && !await ConvertToObservationAsync(state, admission, lifetime.Token)) break;
                    if (!ReceptionistSidebandProtocol.SessionMatches(data, context.ProviderSessionId, settings.Model,
                        required: type is "session.started" or "session.closed", requireModel: type == "session.started"))
                    {
                        await admission.RequestRuntimeStopAsync(callId, "invalid_provider_session", lifetime.Token);
                        await StopAsync(callId, null, lifetime.Token);
                        continue;
                    }
                    var receipt = "openai_event:" + ReceptionistConfigurationCodec.Hash(eventId);
                    if (type == "session.started")
                    {
                        await admission.RecordProviderStateAsync(callId, "accepted", receipt, ct: lifetime.Token);
                        if (runtime.Enabled && businessReady && state.Lease.ObservationOnly)
                        {
                            if (await PromoteObservationAsync(state, admission, lifetime.Token))
                            {
                                state.Context = await admission.GetCallContextAsync(callId, lifetime.Token) ?? state.Context;
                                backendTasks.Add(StartApplicationAsync(state, services, socket, sends, admission, lifetime.Token));
                            }
                        }
                    }
                    if (type == "session.closed")
                    {
                        // One transaction retains authenticated closure plus optional final usage; an ambiguous monthly split stays held.
                        long? finalSeconds = OpenAiReceptionistProtocol.TryCumulativeUsage(data, out var total, out _) && total <= long.MaxValue
                            ? decimal.ToInt64(decimal.Ceiling(total)) : null;
                        await admission.RecordProviderClosureAndUsageAsync(callId, receipt, clock.GetUtcNow().UtcDateTime,
                            finalSeconds, lifetime.Token);
                        state.Closed = true;
                        break;
                    }
                    if (OpenAiReceptionistProtocol.TryCumulativeUsage(data, out var seconds, out _))
                    {
                        if (seconds > long.MaxValue) throw new InvalidOperationException("Voice usage exceeds the accounting representation.");
                        // Round the cumulative duration up once for the integral minute guard; monetary finality remains separate.
                        try { await admission.ReconcileVoiceUsageAsync(callId, receipt, decimal.ToInt64(decimal.Ceiling(seconds)), false, lifetime.Token); }
                        catch (InvalidOperationException)
                        {
                            // A dated-allocation hold commits before refusal. Stop dialogue, retain the hold and keep observing closure.
                            await admission.RequestRuntimeStopAsync(callId, "accounting_unresolved", lifetime.Token);
                            if (!await ConvertToObservationAsync(state, admission, lifetime.Token)) break;
                            await StopAsync(callId, null, lifetime.Token);
                        }
                    }
                    if (state.Lease.ObservationOnly)
                    {
                        // Live events can be buffered. Their arrival time cannot authorize retrying an uncertain stop.
                        await StopAsync(callId, null, lifetime.Token);
                        continue;
                    }
                    state.Application?.Observe(data);
                    if (type == "session.delegation.created" && data.TryGetProperty("delegation", out var delegation) &&
                        OpenAiReceptionistProtocol.String(delegation, "target", out var target) && target == "client" &&
                        OpenAiReceptionistProtocol.String(delegation, "id", out var delegationId) && OpenAiReceptionistProtocol.Token(delegationId, 200))
                    {
                        backendTasks.RemoveAll(x => x.IsCompletedSuccessfully);
                        if (backendTasks.Count >= 8) throw new InvalidOperationException("The backend task queue exceeded its bound.");
                        backendTasks.Add(HandleDelegationAsync(state, delegationId, socket, sends, admission, lifetime.Token));
                    }
                }
                if (!state.Closed && !hostToken.IsCancellationRequested)
                {
                    await admission.RequestRuntimeStopAsync(callId, "transport_lost", hostToken);
                    await StopAsync(callId, null, hostToken);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or WebSocketException)
            {
                if (!hostToken.IsCancellationRequested)
                {
                    await admission.RequestRuntimeStopAsync(callId, "protocol_limit", hostToken);
                    await StopAsync(callId, null, hostToken);
                }
            }
            finally
            {
                lifetime.Cancel();
                socket.Abort();
                try { await Task.WhenAll(backendTasks.Append(heartbeat)); }
                catch (OperationCanceledException) { }
                state.Application?.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            logger.LogWarning("Receptionist call {CallId} requires durable reconciliation.", callId);
        }
    }

    private async Task StartApplicationAsync(SessionState state, IServiceProvider services, WebSocket socket,
        SemaphoreSlim sends, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        try
        {
            state.Application?.Dispose();
            var token = state.Lease.Token;
            var application = services.GetRequiredService<ReceptionistApplicationServices>().CreateSession(state.Context, token);
            state.Application = application;
            var updates = await application.InitializeAsync(ct);
            await SendUpdatesAsync(state, application, token, updates, socket, sends, admission, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await admission.RequestRuntimeStopAsync(state.Context.CallId, "backend_unavailable", ct);
            await StopAsync(state.Context.CallId, null, ct);
        }
    }

    private async Task HandleDelegationAsync(SessionState state, string delegationId, WebSocket socket,
        SemaphoreSlim sends, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        try
        {
            var application = state.Application;
            var token = state.Lease.Token;
            if (application is null || state.Lease.ObservationOnly) return;
            var updates = await application.HandleDelegationAsync(delegationId, ct);
            await SendUpdatesAsync(state, application, token, updates, socket, sends, admission, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await admission.RequestRuntimeStopAsync(state.Context.CallId, "backend_unavailable", ct);
            await StopAsync(state.Context.CallId, null, ct);
        }
    }

    private static async Task SendUpdatesAsync(SessionState state, ReceptionistApplicationSession application, Guid token,
        IReadOnlyList<ReceptionistApplicationUpdate> updates,
        WebSocket socket, SemaphoreSlim sends, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        foreach (var update in updates)
        {
            var body = ReceptionistSidebandProtocol.Append(update.EventId, update.DelegationId, update.Type, update.Content);
            await sends.WaitAsync(ct);
            try
            {
                if (!ReferenceEquals(state.Application, application) || state.Lease.Token != token || state.Lease.ObservationOnly ||
                    !await admission.ValidateSidebandLeaseAsync(state.Context.CallId, token, ct)) return;
                await socket.SendAsync(body.AsMemory(), WebSocketMessageType.Text, true, ct);
            }
            finally { sends.Release(); }
        }
    }

    private async Task HeartbeatAsync(SessionState state, ReceptionistAdmissionService admission,
        ReceptionistRuntimeAuthority runtime, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), clock, lifetime.Token);
                if (clock.GetUtcNow().UtcDateTime >= state.ObserveUntil) { lifetime.Cancel(); break; }
                if (!runtime.Enabled && !state.Lease.ObservationOnly)
                {
                    await admission.RequestRuntimeStopAsync(state.Context.CallId, "runtime_disabled", lifetime.Token);
                    await StopAsync(state.Context.CallId, null, lifetime.Token);
                }
                var owns = false;
                await state.LeaseChanges.WaitAsync(lifetime.Token);
                try
                {
                    var lease = state.Lease;
                    var renewed = lease.ObservationOnly
                        ? await admission.RenewObservationLeaseAsync(state.Context.CallId, _workerId, lease.Token, LeaseDuration, lifetime.Token)
                        : await admission.RenewSidebandLeaseAsync(state.Context.CallId, _workerId, lease.Token, LeaseDuration, lifetime.Token);
                    if (renewed is not null) { state.Lease = renewed; owns = true; }
                    else owns = await ConvertToObservationCoreAsync(state, admission, lifetime.Token);
                }
                finally { state.LeaseChanges.Release(); }
                if (!owns) { lifetime.Cancel(); break; }
                if (state.Lease.ObservationOnly) await StopAsync(state.Context.CallId, null, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { lifetime.Cancel(); }
    }

    private async Task<bool> ConvertToObservationAsync(SessionState state, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        await state.LeaseChanges.WaitAsync(ct);
        try { return await ConvertToObservationCoreAsync(state, admission, ct); }
        finally { state.LeaseChanges.Release(); }
    }

    private async Task<bool> ConvertToObservationCoreAsync(SessionState state, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        if (state.Lease.ObservationOnly)
            return await admission.ValidateObservationLeaseAsync(state.Context.CallId, state.Lease.Token, ct);
        // A concurrent verified promotion may already have replaced the token whose validation failed.
        if (await admission.ValidateSidebandLeaseAsync(state.Context.CallId, state.Lease.Token, ct)) return true;
        var lease = await admission.ConvertSidebandToObservationAsync(state.Context.CallId, _workerId, state.Lease.Token, LeaseDuration, ct);
        if (lease is null) return false;
        state.Lease = lease;
        return true;
    }

    private async Task<bool> PromoteObservationAsync(SessionState state, ReceptionistAdmissionService admission, CancellationToken ct)
    {
        await state.LeaseChanges.WaitAsync(ct);
        try
        {
            if (!state.Lease.ObservationOnly) return false;
            var promoted = await admission.PromoteObservationLeaseAsync(state.Context.CallId, _workerId,
                state.Lease.Token, LeaseDuration, ct);
            if (promoted is null) return false;
            state.Lease = promoted;
            return true;
        }
        finally { state.LeaseChanges.Release(); }
    }

    private async Task StopAsync(Guid callId, ReceptionistVerifiedProviderObservation? observation, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var admission = scope.ServiceProvider.GetRequiredService<ReceptionistAdmissionService>();
            var context = await admission.GetCallContextAsync(callId, ct);
            if (context is null || context.State is "closed" or "rejected") return;
            var runtime = scope.ServiceProvider.GetRequiredService<ReceptionistRuntimeAuthority>();
            var credential = runtime.Credential(context.Owner, context.Binding, webhook: false);
            var claim = await admission.ClaimProviderStopAsync(callId, _workerId, observation, ct);
            if (!claim.MayExecute || claim.ActionId is not { } actionId) return;
            var outcome = await scope.ServiceProvider.GetRequiredService<IOpenAiReceptionistClient>()
                .HangupAsync(context.ProviderProjectKey, context.ProviderSessionId, credential, ct);
            await admission.RecordActionOutcomeAsync(actionId, "uncertain", null, outcome.Receipt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("Receptionist stop for call {CallId} remains unverified.", callId); }
    }

    private sealed class SessionState(ReceptionistCallContext context, ReceptionistSidebandLease lease, DateTime observeUntil)
    {
        internal ReceptionistCallContext Context { get; set; } = context;
        internal ReceptionistSidebandLease Lease = lease;
        internal readonly SemaphoreSlim LeaseChanges = new(1, 1);
        internal DateTime ObserveUntil { get; } = observeUntil;
        internal ReceptionistApplicationSession? Application;
        internal bool Closed;
    }
}
