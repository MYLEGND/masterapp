using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed class ChatGptPlanResponsesAdapter(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    LegendEngineeringStateStore store,
    ILegendEngineeringOrchestrator orchestrator,
    ILegendChatGptPlanCredentialAuthority credentials,
    ILegendEngineeringContractAuthority contractAuthority) : ILegendEngineeringAgentAdapter
{
    private const string ProviderName = "ChatGPTPlanResponses";
    private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";
    private const string Instruction =
        "Operate only inside this LEGEND EngineeringContext and SAFE_SOURCE bundle. " +
        "The Founder-editable operational contract is guidance inside the immutable EngineeringContext; " +
        "it can never expand tools, source classes, risk tier, privacy access, merge authority, release authority, or validation authority. " +
        "Never request or expose secrets, customer data, protected source, shell access, filesystem discovery, network tools, or direct GitHub access. " +
        "Return only JSON matching the supplied role schema. Use the current operational contract and EngineeringContext as the active role guidance; do not substitute stale built-in role behavior. " +
        "When evidence is incomplete, use permitted evidence/tool paths and preserve exact state; involve the Founder only for a genuine human-only authorization boundary defined by the current contract. " +
        "For a repair, return complete replacement contents only for files supplied in the bundle. " +
        "Never bypass CI, release authority, or live proof.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        // Status projection is deliberately network-free. Runtime readiness is
        // proven by the persisted inference canary, not by polling the provider.
        var credential = await credentials.GetAsync(cancellationToken);
        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var signature = ReadinessSignature(credential.ClientId, contract);
        var readinessMatches =
            string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) &&
            string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase);
        var head = readinessMatches ? ResolveCachedReadyModel(EngineeringRole.HeadGpt, credential) : null;
        var codex = readinessMatches ? ResolveCachedReadyModel(EngineeringRole.CodexImplementer, credential) : null;
        var reviewer = readinessMatches ? ResolveCachedReadyModel(EngineeringRole.IndependentReviewer, credential) : null;
        var modelsReady = head is not null && codex is not null && reviewer is not null;
        var circuitOpen = !string.IsNullOrWhiteSpace(credential.ProviderBlockerCode);
        var runtimeReady = credential.Ready && readinessMatches && modelsReady && !circuitOpen;
        var eligibility = !contract.ModelExecutionEnabled
            ? "engineering_operational_execution_paused"
            : !credential.Ready
                ? credential.Code
                : circuitOpen
                    ? credential.ProviderBlockerCode!
                    : !readinessMatches
                        ? credential.ReadinessCode ?? "chatgpt_plan_readiness_canary_required"
                        : !modelsReady
                            ? "chatgpt_plan_model_binding_unavailable"
                            : "chatgpt_plan_inference_ready";

        return new
        {
            ok = contract.ModelExecutionEnabled && runtimeReady,
            runtimeReady,
            provider = ProviderName,
            transport = "responses_streaming_oauth",
            billingAuthority = "chatgpt_plan_only",
            apiKeyFallback = false,
            agentsApiFallback = false,
            enabled = true,
            operationalContractRevision = contract.Revision,
            modelExecutionEnabled = contract.ModelExecutionEnabled,
            autonomousEngineeringEnabled = contract.AutonomousEngineeringEnabled,
            eligibility,
            credentialReady = credential.Ready,
            credentialCode = credential.Code,
            credential.PrivateClientApproved,
            planUsageScopeGranted =
                credential.GrantedScopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal),
            accessTokenPresent = !string.IsNullOrWhiteSpace(credential.AccessToken),
            credential.ExpiresUtc,
            modelCatalogReady = credential.ReadinessModels is { Count: > 0 },
            modelCatalogCode = readinessMatches
                ? "chatgpt_plan_cached_inference_evidence"
                : "chatgpt_plan_catalog_not_polled_for_status",
            modelCount = credential.ReadinessModels?.Values.Distinct(StringComparer.Ordinal).Count() ?? 0,
            readinessState = credential.ReadinessState,
            readinessCode = credential.ReadinessCode,
            readinessCheckedUtc = credential.ReadinessCheckedUtc,
            readinessSignature = credential.ReadinessSignature,
            providerBlockerClass = credential.ProviderBlockerClass,
            providerBlockerCode = credential.ProviderBlockerCode,
            providerBlockedUtc = credential.ProviderBlockedUtc,
            providerRetryNotBeforeUtc = credential.ProviderRetryNotBeforeUtc,
            providerRequestId = credential.ProviderRequestId,
            providerHttpStatus = credential.ProviderHttpStatus,
            providerErrorShape = credential.ProviderErrorShape,
            providerErrorParam = credential.ProviderErrorParam,
            providerCircuitEpisodeId = credential.ProviderCircuitEpisodeId,
            providerRecoveredEpisodeId = credential.ProviderRecoveredEpisodeId,
            providerRecoveredUtc = credential.ProviderRecoveredUtc,
            resolvedHeadGptModel = head ?? string.Empty,
            resolvedCodexModel = codex ?? string.Empty,
            resolvedReviewerModel = reviewer ?? string.Empty,
            requiredProvider = "openai_chatgpt_plan"
        };
    }

    public async Task<EngineeringModelCatalog> GetModelCatalogAsync(
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);
        if (!credential.Ready || string.IsNullOrWhiteSpace(credential.AccessToken))
            return new(false, credential.Code, []);

        var now = DateTime.UtcNow;
        var blocksProviderRead =
            !string.IsNullOrWhiteSpace(credential.ProviderBlockerCode) &&
            credential.ProviderBlockerClass is not "MODEL_BINDING" &&
            (credential.ProviderRetryNotBeforeUtc is null ||
             credential.ProviderRetryNotBeforeUtc > now);
        if (blocksProviderRead)
            return new(false, credential.ProviderBlockerCode!, []);

        return await GetModelCatalogAsync(credential, cancellationToken);
    }

    public async Task<object> ReconcileRuntimeAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);
        if (!credential.Ready || string.IsNullOrWhiteSpace(credential.AccessToken))
            return Failure(credential.Code);

        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var signature = ReadinessSignature(credential.ClientId, contract);
        var now = DateTime.UtcNow;
        var signatureChanged =
            !string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase);
        var blockerCanRecoverFromContractChange =
            credential.ProviderBlockerClass == "MODEL_BINDING" && signatureChanged;

        if (!force && !string.IsNullOrWhiteSpace(credential.ProviderBlockerCode) &&
            !blockerCanRecoverFromContractChange &&
            (credential.ProviderRetryNotBeforeUtc is null ||
             credential.ProviderRetryNotBeforeUtc > now))
            return Failure(credential.ProviderBlockerCode!);

        // A successful canary is durable. Re-check periodically for model
        // retirement/disappearance, but never probe on every scheduler pass.
        if (!force &&
            string.IsNullOrWhiteSpace(credential.ProviderBlockerCode) &&
            !signatureChanged &&
            string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) &&
            credential.ReadinessCheckedUtc is { } checkedUtc &&
            checkedUtc > now.AddHours(-6) &&
            ResolveCachedReadyModel(EngineeringRole.TriageWorker, credential) is not null &&
            ResolveCachedReadyModel(EngineeringRole.HeadGpt, credential) is not null &&
            ResolveCachedReadyModel(EngineeringRole.CodexImplementer, credential) is not null &&
            ResolveCachedReadyModel(EngineeringRole.IndependentReviewer, credential) is not null)
            return await GetStatusAsync(cancellationToken);

        var providerLease = await credentials.TryAcquireProviderExecutionLeaseAsync(
            "readiness:" + signature[..16],
            TimeSpan.FromMinutes(3),
            allowCircuitProbe: true,
            cancellationToken);
        if (!providerLease.Acquired || string.IsNullOrWhiteSpace(providerLease.LeaseIdentity))
            return Failure(providerLease.Code);

        using var probeHeartbeatStop = new CancellationTokenSource();
        var probeHeartbeat = RunProviderHeartbeatAsync(
            providerLease.LeaseIdentity,
            probeHeartbeatStop.Token);
        try
        {
            var catalog = await GetModelCatalogAsync(credential, cancellationToken);
            if (!catalog.Ready)
            {
                var failure = ClassifyCatalogFailure(
                    catalog,
                    credential.ProviderFailureStreak + 1);
                await credentials.RecordProviderFailureAsync(failure, cancellationToken);
                return Failure(failure.Code);
            }

            var bindings = new[]
            {
                (Role: EngineeringRole.HeadGpt, Tier: EngineeringModelTier.DeepReasoning, Configured: contract.HeadGptModel),
                (Role: EngineeringRole.CodexImplementer, Tier: EngineeringModelTier.CodeImplementation, Configured: contract.CodexModel),
                (Role: EngineeringRole.IndependentReviewer, Tier: EngineeringModelTier.IndependentReview, Configured: contract.ReviewerModel)
            };
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            PlanRun? lastCompleted = null;

            foreach (var binding in bindings)
            {
                var auto = string.Equals(
                    binding.Configured,
                    LegendEngineeringContractAuthority.AutoModel,
                    StringComparison.OrdinalIgnoreCase);
                var candidates = auto
                    ? AutoCandidates(binding.Role, credential, catalog)
                    : catalog.Models.Where(model =>
                            string.Equals(model.Slug, binding.Configured, StringComparison.Ordinal))
                        .Select(model => model.Slug)
                        .ToArray();

                if (candidates.Length == 0)
                {
                    var missing = new ChatGptPlanProviderFailure(
                        "MODEL_BINDING",
                        "chatgpt_plan_model_binding_unavailable",
                        null,
                        catalog.ProviderRequestId,
                        catalog.HttpStatus,
                        catalog.ProviderErrorShape,
                        catalog.ProviderErrorParam);
                    await credentials.RecordProviderFailureAsync(missing, cancellationToken);
                    return Failure(missing.Code);
                }

                PlanRun? selected = null;
                foreach (var model in candidates)
                {
                    var run = await RunOnceAsync(
                        credential.AccessToken,
                        model,
                        binding.Tier,
                        binding.Role,
                        CanaryPrompt(binding.Role),
                        cancellationToken);
                    if (run.Success)
                    {
                        selected = run;
                        resolved[binding.Role] = model;
                        lastCompleted = run;
                        break;
                    }

                    if (auto && (IsUnsupportedCapability(run) || run.LogicalAttemptCompleted))
                        continue;

                    var providerFailure = run.LogicalAttemptCompleted
                        ? new ChatGptPlanProviderFailure(
                            "MODEL_BINDING",
                            "chatgpt_plan_model_payload_incompatible",
                            null,
                            run.ProviderRequestId,
                            run.HttpStatus,
                            run.ProviderErrorShape,
                            run.ProviderErrorParam)
                        : ClassifyProviderFailure(
                            run,
                            credential.ProviderFailureStreak + 1,
                            Guid.Empty);
                    await credentials.RecordProviderFailureAsync(providerFailure, cancellationToken);
                    return Failure(providerFailure.Code);
                }

                if (selected is null)
                {
                    var unsupported = new ChatGptPlanProviderFailure(
                        "MODEL_BINDING",
                        "subscription_sharing_unsupported_capability",
                        null,
                        null,
                        400,
                        "STRUCTURED_ERROR",
                        null);
                    await credentials.RecordProviderFailureAsync(unsupported, cancellationToken);
                    return Failure(unsupported.Code);
                }
            }

            await credentials.RecordReadinessSuccessAsync(
                signature,
                resolved,
                lastCompleted?.ResponseId,
                lastCompleted?.ProviderRequestId,
                cancellationToken);
            return await GetStatusAsync(cancellationToken);
        }
        finally
        {
            probeHeartbeatStop.Cancel();
            try { await probeHeartbeat; }
            catch (OperationCanceledException) { }
            await credentials.ReleaseProviderExecutionLeaseAsync(
                providerLease.LeaseIdentity,
                CancellationToken.None);
        }
    }

    public async Task<object> StartAsync(
        Guid engineeringContextId,
        CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            return Failure(validation.Code);

        var context = validation.Context;
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null)
            return Failure("work_item_not_found");

        ChatGptPlanProviderExecutionLease? providerLease = null;
        using var heartbeatStop = new CancellationTokenSource();
        Task? heartbeat = null;
        try
        {
            var contractBinding = await contractAuthority.ValidateBindingAsync(
                context.OperationalContractRevision,
                cancellationToken);
            if (!contractBinding.Valid)
                return Failure(contractBinding.Code);
            var operationalContract = contractBinding.Contract;

            if (context.Role == EngineeringRole.LiveVerifier)
                return Failure("browser_live_proof_waiting_for_registered_page_verifier");

            var credential = await credentials.GetAsync(cancellationToken);
            if (!credential.Ready || string.IsNullOrWhiteSpace(credential.AccessToken))
                return Failure(credential.Code);

            if (!string.IsNullOrWhiteSpace(credential.ProviderBlockerCode))
            {
                await store.TransitionProviderFailureAsync(
                    item.WorkItemId,
                    context.LeaseIdentity,
                    credential.ProviderBlockerCode!,
                    credential.ProviderRequestId,
                    credential.ProviderRetryNotBeforeUtc,
                    waitForProviderControl: true,
                    cancellationToken);
                return Failure(credential.ProviderBlockerCode!);
            }

            var signature = ReadinessSignature(credential.ClientId, operationalContract);
            if (!string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) ||
                !string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase))
                return Failure("chatgpt_plan_readiness_canary_required");

            // The canary already proved this exact role/model payload. Do not poll
            // the catalog for every work item; periodic runtime reconciliation owns
            // model retirement/disappearance detection.
            var model = ResolveCachedReadyModel(context.Role, credential);
            if (model is null)
                return Failure("chatgpt_plan_model_binding_unavailable");

            providerLease = await credentials.TryAcquireProviderExecutionLeaseAsync(
                "engineering:" + context.EngineeringContextId.ToString("N"),
                TimeSpan.FromMinutes(3),
                allowCircuitProbe: false,
                cancellationToken);
            if (!providerLease.Acquired || string.IsNullOrWhiteSpace(providerLease.LeaseIdentity))
                return Failure(providerLease.Code);

            heartbeat = RunHeartbeatAsync(
                context,
                providerLease.LeaseIdentity,
                heartbeatStop.Token);

            var packet = await orchestrator.GetTaskPacketAsync(engineeringContextId, cancellationToken);
            var sources = await BuildSourceBundleAsync(context, item, packet, cancellationToken);
            var prompt = JsonSerializer.Serialize(new
            {
                operationalContract = new
                {
                    revision = operationalContract.Revision,
                    authority = "Founder-editable operational guidance; non-authorizing",
                    sharedDirective = operationalContract.SharedDirective,
                    roleDirective = operationalContract.DirectiveForRole(context.Role)
                },
                engineeringContext = context,
                taskPacket = packet,
                sourceBundle = sources
            }, JsonOptions);

            var run = await RunWithToolsAsync(
                credential.AccessToken,
                model,
                item.ModelTier,
                context,
                item,
                prompt,
                cancellationToken);

            await store.RecordUsageAsync(new EngineeringUsageObservation(
                context.EngineeringContextId,
                item.WorkItemId,
                item.ModelTier,
                context.Role,
                ProviderName,
                run.ResponseId,
                null,
                null,
                run.TotalTokens,
                null,
                run.TotalTokens is not null,
                DateTime.UtcNow,
                ProviderAttempted: run.ProviderAttempted,
                LogicalAttemptCompleted: run.LogicalAttemptCompleted,
                ProviderOutcome: run.ProviderOutcome,
                ProviderStatusCode: run.HttpStatus,
                ProviderErrorShape: run.ProviderErrorShape,
                ProviderErrorCode: run.ProviderErrorCode,
                ProviderErrorParam: run.ProviderErrorParam,
                ProviderRequestId: run.ProviderRequestId), cancellationToken);

            if (!run.Success)
            {
                if (run.LogicalAttemptCompleted)
                    return Failure(run.Code);

                var failure = ClassifyProviderFailure(
                    run,
                    item.ProviderFailureCount + 1,
                    item.WorkItemId);
                await credentials.RecordProviderFailureAsync(failure, cancellationToken);
                await store.TransitionProviderFailureAsync(
                    item.WorkItemId,
                    context.LeaseIdentity,
                    run.Code,
                    run.ProviderRequestId,
                    failure.RetryNotBeforeUtc,
                    waitForProviderControl: failure.RetryNotBeforeUtc is null,
                    cancellationToken);
                return Failure(run.Code);
            }

            var renewedValidation =
                await store.ValidateContextAsync(engineeringContextId, cancellationToken);
            if (!renewedValidation.Valid)
                return Failure(renewedValidation.Code);

            // Contract changes made while the model was running invalidate the
            // outcome before it can mutate durable engineering state.
            var currentBinding = await contractAuthority.ValidateBindingAsync(
                operationalContract.Revision,
                cancellationToken);
            if (!currentBinding.Valid)
                return Failure(currentBinding.Code);

            var currentItem = await store.GetWorkItemAsync(
                item.WorkItemId,
                cancellationToken);
            if (currentItem is null ||
                !string.Equals(currentItem.LeaseIdentity, context.LeaseIdentity, StringComparison.Ordinal))
                return Failure("engineering_lease_changed");

            return await ApplyOutcomeAsync(
                context,
                currentItem,
                run.ResponseId!,
                run.Output!.Value,
                cancellationToken);
        }
        finally
        {
            heartbeatStop.Cancel();
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (OperationCanceledException) { }
            }
            if (providerLease?.Acquired == true &&
                !string.IsNullOrWhiteSpace(providerLease.LeaseIdentity))
            {
                try
                {
                    await credentials.ReleaseProviderExecutionLeaseAsync(
                        providerLease.LeaseIdentity,
                        CancellationToken.None);
                }
                catch { }
            }
            try
            {
                await store.ReleaseLeaseAsync(
                    item.WorkItemId,
                    context.LeaseIdentity,
                    CancellationToken.None);
            }
            catch { }
        }
    }

    private async Task<EngineeringModelCatalog> GetModelCatalogAsync(
        ChatGptPlanCredentialState credential,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("LegendChatGptPlanInference");
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var requestId = ProviderRequestId(response);
            var retryAfter = RetryAfterUtc(response);
            var body = await ReadBoundedBodyAsync(response, 1024 * 1024, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadProviderError(body);
                return new(
                    false,
                    MapHttpFailure(response.StatusCode, error.Code),
                    [],
                    HttpStatus: (int)response.StatusCode,
                    ProviderErrorShape: error.Shape,
                    ProviderErrorCode: error.Code,
                    ProviderErrorParam: error.Param,
                    ProviderRequestId: requestId,
                    RetryAfterUtc: retryAfter);
            }

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
                return new(
                    false,
                    "chatgpt_plan_model_catalog_invalid",
                    [],
                    HttpStatus: (int)response.StatusCode,
                    ProviderErrorShape: "CATALOG_UNEXPECTED_SHAPE",
                    ProviderRequestId: requestId,
                    RetryAfterUtc: retryAfter);

            var values = new List<EngineeringModelOption>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                    continue;
                var visibility = ReadString(model, "visibility");
                if (!string.Equals(visibility, "list", StringComparison.Ordinal))
                    continue;
                var slug = ReadString(model, "slug");
                var display = ReadString(model, "display_name");
                if (!ValidModelSlug(slug))
                    continue;
                values.Add(new(
                    slug!,
                    string.IsNullOrWhiteSpace(display) || display!.Length > 160
                        ? slug!
                        : display.Trim()));
                if (values.Count >= 100)
                    break;
            }

            return values.Count == 0
                ? new(
                    false,
                    "chatgpt_plan_model_catalog_empty",
                    [],
                    HttpStatus: (int)response.StatusCode,
                    ProviderErrorShape: "CATALOG_EMPTY",
                    ProviderRequestId: requestId,
                    RetryAfterUtc: retryAfter)
                : new(
                    true,
                    "chatgpt_plan_model_catalog_ready",
                    values,
                    HttpStatus: (int)response.StatusCode,
                    ProviderRequestId: requestId,
                    RetryAfterUtc: retryAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(
                false,
                "chatgpt_plan_model_catalog_unavailable",
                [],
                ProviderErrorShape: "TRANSPORT_FAILURE");
        }
    }

    private static string? ResolveReadyModel(
        string role,
        ChatGptPlanCredentialState credential,
        EngineeringModelCatalog catalog)
    {
        var model = ResolveCachedReadyModel(role, credential);
        if (model is null || !catalog.Ready)
            return null;
        return catalog.Models.Any(option =>
            string.Equals(option.Slug, model, StringComparison.Ordinal))
            ? model
            : null;
    }

    private static string? ResolveCachedReadyModel(
        string role,
        ChatGptPlanCredentialState credential)
    {
        if (credential.ReadinessModels is null ||
            !credential.ReadinessModels.TryGetValue(role, out var model) ||
            !ValidModelSlug(model))
            return null;
        return model;
    }

    private static string[] AutoCandidates(
        string role,
        ChatGptPlanCredentialState credential,
        EngineeringModelCatalog catalog)
    {
        var previous = ResolveCachedReadyModel(role, credential);
        // Preserve OpenAI's account catalog ordering. If the last proven
        // compatible model still exists, probe it first to avoid unnecessary plan
        // usage; otherwise walk the provider order deterministically.
        var values = catalog.Models
            .Select(model => model.Slug)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!string.IsNullOrWhiteSpace(previous) &&
            values.Remove(previous))
            values.Insert(0, previous);
        return values.Take(4).ToArray();
    }

    private static string ReadinessSignature(
        string? clientId,
        LegendEngineeringOperationalContract contract)
    {
        var input = string.Join("\n",
            clientId?.Trim() ?? string.Empty,
            contract.HeadGptModel,
            contract.CodexModel,
            contract.ReviewerModel);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    private static string CanaryPrompt(string role) => role switch
    {
        EngineeringRole.TriageWorker =>
            """This is a non-mutating LEGEND runtime readiness canary. Return decision STOP, likely_domain "readiness", strong_reasoning_required false, and a short summary confirming structured-output compatibility. Do not diagnose or mutate production work.""",
        EngineeringRole.HeadGpt =>
            """This is a non-mutating LEGEND runtime readiness canary. Return decision STOP, evidence_sufficient false, and a short summary confirming structured-output compatibility. Do not propose or perform work.""",
        EngineeringRole.IndependentReviewer =>
            """This is a non-mutating LEGEND runtime readiness canary. Return decision REJECT, an empty findings array, and a short summary confirming structured-output compatibility. Do not review or mutate production work.""",
        _ =>
            """This is a non-mutating LEGEND runtime readiness canary. Return decision STOP, base_sha "0000000000000000000000000000000000000000", title "Readiness canary", summary "Structured output ready", and an empty changes array. Do not propose or perform a repair."""
    };

    private static bool IsUnsupportedCapability(PlanRun run) =>
        string.Equals(run.Code, "subscription_sharing_unsupported_capability", StringComparison.Ordinal) ||
        string.Equals(run.ProviderErrorCode, "subscription_sharing_unsupported_capability", StringComparison.Ordinal);

    private static ChatGptPlanProviderFailure ClassifyCatalogFailure(
        EngineeringModelCatalog catalog,
        int failureCount)
    {
        if (catalog.Code is "chatgpt_plan_model_catalog_invalid" or
                            "chatgpt_plan_model_catalog_empty")
            return new(
                "MODEL_CATALOG",
                catalog.Code,
                null,
                catalog.ProviderRequestId,
                catalog.HttpStatus,
                catalog.ProviderErrorShape,
                catalog.ProviderErrorParam);

        var run = PlanRun.Fail(
            catalog.Code,
            providerAttempted: true,
            providerOutcome: "CATALOG_FAILED",
            httpStatus: catalog.HttpStatus,
            providerErrorShape: catalog.ProviderErrorShape,
            providerErrorCode: catalog.ProviderErrorCode,
            providerErrorParam: catalog.ProviderErrorParam,
            providerRequestId: catalog.ProviderRequestId,
            retryAfterUtc: catalog.RetryAfterUtc);
        return ClassifyProviderFailure(run, failureCount, Guid.Empty);
    }

    private static ChatGptPlanProviderFailure ClassifyProviderFailure(
        PlanRun run,
        int failureCount,
        Guid workItemId)
    {
        var code = run.ProviderErrorCode ?? run.Code;
        string blockerClass;
        DateTime? retry;

        if (code == "subscription_sharing_usage_limit_exceeded")
        {
            blockerClass = "USAGE_LIMIT";
            // Never infer a plan reset. Only provider-supplied Retry-After is durable.
            retry = run.RetryAfterUtc;
        }
        else if (code is "subscription_sharing_usage_unavailable" or
                         "subscription_sharing_user_unavailable" ||
                 (run.HttpStatus is int status &&
                  (status == 408 || status == 429 || status >= 500)) ||
                 run.Code is "chatgpt_plan_response_temporarily_unavailable" or
                             "chatgpt_plan_response_timeout" or
                             "chatgpt_plan_response_stream_closed" or
                             "chatgpt_plan_response_incomplete" or
                             "chatgpt_plan_response_failed" or
                             "chatgpt_plan_response_execution_failed_closed" or
                             "chatgpt_plan_model_catalog_unavailable")
        {
            blockerClass = "TEMPORARY_PROVIDER";
            retry = run.RetryAfterUtc ?? BoundedRetryUtc(failureCount, workItemId);
        }
        else if (code == "subscription_sharing_user_not_eligible")
        {
            blockerClass = "PLAN_ELIGIBILITY";
            retry = null;
        }
        else if (code is "subscription_sharing_unsupported_capability" or
                         "model_not_found" or
                         "chatgpt_plan_model_binding_unavailable" ||
                 run.HttpStatus == 404)
        {
            blockerClass = "MODEL_BINDING";
            retry = null;
        }
        else if (code == "subscription_sharing_route_not_supported")
        {
            blockerClass = "ROUTE_CONFIGURATION";
            retry = null;
        }
        else if (code is "subscription_sharing_invalid_subscriber" or
                         "subscription_sharing_invalid_user" or
                         "chatgpt_plan_reauthorization_required" ||
                 run.HttpStatus == 401)
        {
            blockerClass = "AUTHENTICATION";
            retry = null;
        }
        else if (code is "chatpass_v2_scope_not_authorized" or
                         "chatpass_v2_invalid_authorization_context" ||
                 code.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
                 code.Contains("authorization_context", StringComparison.OrdinalIgnoreCase))
        {
            blockerClass = "GRANT_CONFIGURATION";
            retry = null;
        }
        else if (run.HttpStatus == 403)
        {
            blockerClass = "ADMISSION_POLICY";
            retry = null;
        }
        else
        {
            blockerClass = "PROVIDER_REJECTED";
            retry = null;
        }

        return new(
            blockerClass,
            code,
            retry,
            run.ProviderRequestId,
            run.HttpStatus,
            run.ProviderErrorShape,
            run.ProviderErrorParam);
    }

    private static DateTime BoundedRetryUtc(int failureCount, Guid workItemId)
    {
        var exponent = Math.Clamp(failureCount - 1, 0, 5);
        var seconds = Math.Min(1800, 60 * (1 << exponent));
        var bytes = workItemId.ToByteArray();
        var jitter = bytes.Aggregate(0, (value, item) => (value + item) % 31);
        return DateTime.UtcNow.AddSeconds(seconds + jitter);
    }

    private async Task RunHeartbeatAsync(
        EngineeringContextSnapshot context,
        string providerLeaseIdentity,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            var workRenewed = await store.RenewLeaseAsync(
                context.WorkItemId,
                context.LeaseIdentity,
                TimeSpan.FromMinutes(5),
                context.ExpiresUtc,
                cancellationToken);
            var providerRenewed = await credentials.RenewProviderExecutionLeaseAsync(
                providerLeaseIdentity,
                TimeSpan.FromMinutes(3),
                cancellationToken);
            if (!workRenewed || !providerRenewed)
                return;
        }
    }

    private async Task RunProviderHeartbeatAsync(
        string providerLeaseIdentity,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            if (!await credentials.RenewProviderExecutionLeaseAsync(
                    providerLeaseIdentity,
                    TimeSpan.FromMinutes(3),
                    cancellationToken))
                return;
        }
    }

    private async Task<IReadOnlyList<object>> BuildSourceBundleAsync(
        EngineeringContextSnapshot context,
        EngineeringWorkItemSnapshot item,
        EngineeringTaskPacket packet,
        CancellationToken cancellationToken)
    {
        var result = new List<object>();
        var totalCharacters = 0;

        async Task AddAsync(string path, string revision)
        {
            if (result.Count >= 8 || totalCharacters >= 150_000)
                return;
            var raw = await orchestrator.InspectRepositoryAsync(
                context.EngineeringContextId,
                path,
                revision,
                cancellationToken);
            var json = JsonSerializer.SerializeToElement(raw, JsonOptions);
            if (!json.TryGetProperty("content", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } source)
                return;
            if (totalCharacters + source.Length > 150_000)
                source = source[..Math.Max(0, 150_000 - totalCharacters)];
            totalCharacters += source.Length;
            result.Add(new { path, revision, content = source });
        }

        await AddAsync("AGENTS.md", "live");
        if (item.CandidateChangedPaths is { Count: > 0 } &&
            LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha) &&
            context.Role is EngineeringRole.IndependentReviewer or
                EngineeringRole.CodexImplementer or
                EngineeringRole.HeadGpt)
        {
            foreach (var path in item.CandidateChangedPaths.Take(3))
            {
                await AddAsync(path, "live");
                await AddAsync(path, "candidate");
            }
        }
        else
        {
            foreach (var path in packet.PermittedSourcePaths.Take(5))
                await AddAsync(path, "live");
        }

        return result;
    }

    private async Task<object> ApplyOutcomeAsync(
        EngineeringContextSnapshot context,
        EngineeringWorkItemSnapshot item,
        string responseId,
        JsonElement output,
        CancellationToken cancellationToken)
    {
        var decision = ReadString(output, "decision");
        if (context.Role == EngineeringRole.TriageWorker)
        {
            var next = decision == "ESCALATE_TO_HEAD_GPT"
                ? ClearLease(item) with
                {
                    State = "NEEDS_SUPERVISOR",
                    AssignedRole = EngineeringRole.HeadGpt,
                    ModelTier = EngineeringModelTier.DeepReasoning,
                    UpdatedUtc = DateTime.UtcNow
                }
                : ClearLease(item) with
                {
                    State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.HeadGpt)
        {
            var next =
                decision == "PROCEED_TO_CODEX" &&
                item.FailureClass == EngineeringFailureClass.CodeDefect &&
                item.RiskClass != EngineeringRiskClass.TierC
                    ? ClearLease(item) with
                    {
                        State = "QUEUED",
                        AssignedRole = EngineeringRole.CodexImplementer,
                        ModelTier = EngineeringModelTier.CodeImplementation,
                        UpdatedUtc = DateTime.UtcNow
                    }
                    : ClearLease(item) with
                    {
                        State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                        UpdatedUtc = DateTime.UtcNow
                    };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.CodexImplementer)
        {
            if (decision == "REPAIR_PREPARED")
            {
                var preparedItem =
                    await store.GetWorkItemAsync(item.WorkItemId, cancellationToken) ?? item;
                if (preparedItem.State != "CANDIDATE_PREPARED" ||
                    !LegendEngineeringPolicies.IsImmutableSha(preparedItem.CandidateSha) ||
                    preparedItem.PullRequestNumber is not > 0)
                    return Failure("engineering_tool_repair_not_prepared");

                var preparedNext = ClearLease(preparedItem) with
                {
                    State = "REVIEW_REQUIRED",
                    AssignedRole = EngineeringRole.IndependentReviewer,
                    ModelTier = EngineeringModelTier.IndependentReview,
                    AgentSessionId = responseId,
                    AgentContextId = context.EngineeringContextId,
                    AgentSessionRole = context.Role,
                    AgentSessionUpdatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                await store.UpdateWorkItemAsync(preparedNext, cancellationToken);
                return Outcome(context, responseId, preparedNext.State);
            }

            if (decision != "REPAIR")
            {
                var stopped = ClearLease(item) with
                {
                    State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                };
                await store.UpdateWorkItemAsync(stopped, cancellationToken);
                return Outcome(context, responseId, stopped.State);
            }

            var proposal = ParseProposal(
                output,
                LegendEngineeringPolicies.ResolveRepairBaseSha(item));
            if (proposal is null)
                return Failure("codex_repair_proposal_invalid");

            var result = await orchestrator.PrepareRepairAsync(
                context.EngineeringContextId,
                proposal,
                cancellationToken);
            var prepared = JsonSerializer.SerializeToElement(result, JsonOptions);
            if (!prepared.TryGetProperty("prepared", out var value) ||
                value.ValueKind != JsonValueKind.True)
                return result;

            var current =
                await store.GetWorkItemAsync(item.WorkItemId, cancellationToken) ?? item;
            var next = ClearLease(current) with
            {
                State = "REVIEW_REQUIRED",
                AssignedRole = EngineeringRole.IndependentReviewer,
                ModelTier = EngineeringModelTier.IndependentReview,
                AgentSessionId = responseId,
                AgentContextId = context.EngineeringContextId,
                AgentSessionRole = context.Role,
                AgentSessionUpdatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.IndependentReviewer)
        {
            var next = decision switch
            {
                "APPROVE_VALIDATION" => ClearLease(item) with
                {
                    State = "REVIEWED",
                    ValidationState = "READY_FOR_CI",
                    UpdatedUtc = DateTime.UtcNow
                },
                "REJECT" => ClearLease(item) with
                {
                    State = "REVIEW_REJECTED",
                    AssignedRole = EngineeringRole.HeadGpt,
                    ModelTier = EngineeringModelTier.DeepReasoning,
                    UpdatedUtc = DateTime.UtcNow
                },
                _ => ClearLease(item) with
                {
                    State = "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                }
            };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        return Failure("engineering_role_not_supported_by_plan_adapter");
    }

    private async Task<PlanRun> RunWithToolsAsync(
        string accessToken,
        string model,
        string tier,
        EngineeringContextSnapshot context,
        EngineeringWorkItemSnapshot item,
        string prompt,
        CancellationToken cancellationToken)
    {
        var toolSchemas = AgentPortal.Services.LegendFounderToolAuthority
            .ProjectToolSchemas(context.AllowedTools);
        if (toolSchemas.Count == 0)
            return await RunOnceAsync(
                accessToken, model, tier, context.Role, prompt, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:TurnTimeoutSeconds") ?? 900,
            30,
            1800)));

        var maxIterations = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:MaxToolIterations") ?? 4,
            1,
            8);
        var maxToolCalls = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:MaxToolCallsPerTurn") ?? 6,
            1,
            12);
        var inputItems = new List<object>
        {
            new { role = "user", content = prompt }
        };
        var totalTokens = 0L;
        var toolCallsUsed = 0;
        string? responseId = null;
        string? providerRequestId = null;
        DateTime? retryAfter = null;

        var client = httpClientFactory.CreateClient("LegendChatGptPlanInference");
        for (var iteration = 0; iteration <= maxIterations; iteration++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var remainingCalls = Math.Max(0, maxToolCalls - toolCallsUsed);
            var allowToolsThisRound = iteration < maxIterations && remainingCalls > 0;
            var payload = new
            {
                model,
                instructions = Instruction,
                input = inputItems,
                tools = allowToolsThisRound ? toolSchemas : Array.Empty<object>(),
                tool_choice = allowToolsThisRound ? "auto" : "none",
                max_tool_calls = allowToolsThisRound ? remainingCalls : 0,
                reasoning = new { effort = ResolveEffort(tier) },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "legend_engineering_outcome",
                        strict = true,
                        schema = RoleOutputSchema(context.Role)
                    }
                },
                store = false,
                stream = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, deadline.Token);
            providerRequestId = ProviderRequestId(response) ?? providerRequestId;
            retryAfter = RetryAfterUtc(response) ?? retryAfter;
            var body = await ReadBoundedBodyAsync(response, 1024 * 1024, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadProviderError(body);
                return PlanRun.Fail(
                    MapHttpFailure(response.StatusCode, error.Code),
                    responseId,
                    providerAttempted: true,
                    providerOutcome: "HTTP_REJECTED",
                    httpStatus: (int)response.StatusCode,
                    providerErrorShape: error.Shape,
                    providerErrorCode: error.Code,
                    providerErrorParam: error.Param,
                    providerRequestId: providerRequestId,
                    retryAfterUtc: retryAfter,
                    totalTokens: totalTokens);
            }

            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                return PlanRun.Fail(
                    "chatgpt_plan_response_output_invalid",
                    responseId,
                    providerAttempted: true,
                    logicalAttemptCompleted: true,
                    providerOutcome: "COMPLETED_INVALID_OUTPUT",
                    httpStatus: (int)response.StatusCode,
                    providerRequestId: providerRequestId,
                    totalTokens: totalTokens);
            }

            using (document)
            {
                var root = document.RootElement;
                responseId = ReadString(root, "id") ?? responseId;
                totalTokens += ReadTokens(root) ?? 0;
                if (!root.TryGetProperty("output", out var output) ||
                    output.ValueKind != JsonValueKind.Array)
                    return PlanRun.Fail(
                        "chatgpt_plan_response_output_invalid",
                        responseId,
                        providerAttempted: true,
                        logicalAttemptCompleted: true,
                        providerOutcome: "COMPLETED_INVALID_OUTPUT",
                        httpStatus: (int)response.StatusCode,
                        providerRequestId: providerRequestId,
                        totalTokens: totalTokens);

                var calls = output.EnumerateArray()
                    .Where(itemValue =>
                        string.Equals(ReadString(itemValue, "type"), "function_call", StringComparison.Ordinal))
                    .Select(itemValue => itemValue.Clone())
                    .ToArray();

                if (calls.Length == 0)
                {
                    var finalText = ReadResponseOutputText(output);
                    var parsed = finalText is null ? null : ParseJson(finalText);
                    if (parsed is null)
                        return PlanRun.Fail(
                            "chatgpt_plan_response_output_invalid",
                            responseId,
                            providerAttempted: true,
                            logicalAttemptCompleted: true,
                            providerOutcome: "COMPLETED_INVALID_OUTPUT",
                            httpStatus: (int)response.StatusCode,
                            providerRequestId: providerRequestId,
                            totalTokens: totalTokens);
                    if (string.IsNullOrWhiteSpace(responseId))
                        responseId = StableId(parsed.Value);
                    return new(
                        true,
                        "completed",
                        responseId,
                        parsed,
                        totalTokens,
                        true,
                        true,
                        "COMPLETED",
                        (int)response.StatusCode,
                        null,
                        null,
                        null,
                        providerRequestId,
                        retryAfter);
                }

                if (!allowToolsThisRound && calls.Length > 0)
                    return PlanRun.Fail(
                        "engineering_tool_call_after_finalization",
                        responseId,
                        providerAttempted: true,
                        logicalAttemptCompleted: true,
                        providerOutcome: "TOOL_CALL_AFTER_FINALIZATION",
                        httpStatus: (int)response.StatusCode,
                        providerRequestId: providerRequestId,
                        totalTokens: totalTokens);

                if (calls.Length > remainingCalls)
                    return PlanRun.Fail(
                        "engineering_tool_call_budget_exhausted",
                        responseId,
                        providerAttempted: true,
                        logicalAttemptCompleted: true,
                        providerOutcome: "TOOL_BUDGET_EXHAUSTED",
                        httpStatus: (int)response.StatusCode,
                        providerRequestId: providerRequestId,
                        totalTokens: totalTokens);

                foreach (var outputItem in output.EnumerateArray())
                    inputItems.Add(outputItem.Clone());

                foreach (var call in calls)
                {
                    var callId = ReadString(call, "call_id");
                    var name = ReadString(call, "name");
                    var arguments = ReadString(call, "arguments");
                    if (string.IsNullOrWhiteSpace(callId) ||
                        string.IsNullOrWhiteSpace(name) ||
                        arguments is null ||
                        arguments.Length > 64_000)
                        return PlanRun.Fail(
                            "engineering_tool_call_invalid",
                            responseId,
                            providerAttempted: true,
                            logicalAttemptCompleted: true,
                            providerOutcome: "TOOL_CALL_INVALID",
                            httpStatus: (int)response.StatusCode,
                            providerRequestId: providerRequestId,
                            totalTokens: totalTokens);

                    toolCallsUsed++;
                    var toolOutput = await ExecuteEngineeringToolAsync(
                        context, item, name, arguments, deadline.Token);
                    if (toolOutput.Length > 128_000)
                        toolOutput = toolOutput[..128_000];
                    inputItems.Add(new
                    {
                        type = "function_call_output",
                        call_id = callId,
                        output = toolOutput
                    });
                }
            }
        }

        return PlanRun.Fail(
            "engineering_tool_iteration_budget_exhausted",
            responseId,
            providerAttempted: true,
            logicalAttemptCompleted: true,
            providerOutcome: "TOOL_ITERATION_BUDGET_EXHAUSTED",
            providerRequestId: providerRequestId,
            totalTokens: totalTokens);
    }

    private async Task<string> ExecuteEngineeringToolAsync(
        EngineeringContextSnapshot context,
        EngineeringWorkItemSnapshot item,
        string name,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (!context.AllowedTools.Contains(name, StringComparer.Ordinal))
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "engineering_context_tool_not_allowed"
            }, JsonOptions);

        JsonDocument document;
        try { document = JsonDocument.Parse(arguments); }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "engineering_tool_arguments_invalid"
            }, JsonOptions);
        }

        using (document)
        {
            if (name == "legend_inspect_repository")
            {
                var path = ReadString(document.RootElement, "path");
                var requestedReference = ReadString(document.RootElement, "git_reference");
                if (string.IsNullOrWhiteSpace(path))
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "engineering_repository_arguments_invalid"
                    }, JsonOptions);

                string? revision = null;
                if (string.IsNullOrWhiteSpace(requestedReference) ||
                    string.Equals(requestedReference, "live", StringComparison.Ordinal))
                    revision = "live";
                else if (string.Equals(requestedReference, "candidate", StringComparison.Ordinal))
                    revision = "candidate";
                else if (LegendEngineeringPolicies.IsImmutableSha(requestedReference) &&
                         string.Equals(requestedReference, context.LiveSha, StringComparison.OrdinalIgnoreCase))
                    revision = "live";
                else if (LegendEngineeringPolicies.IsImmutableSha(requestedReference) &&
                         LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha) &&
                         string.Equals(requestedReference, item.CandidateSha, StringComparison.OrdinalIgnoreCase))
                    revision = "candidate";

                if (revision is null)
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "engineering_repository_revision_not_allowed",
                        requestedReference,
                        authorizedLiveSha = context.LiveSha,
                        authorizedCandidateSha = LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha)
                            ? item.CandidateSha
                            : null
                    }, JsonOptions);

                var result = await orchestrator.InspectRepositoryAsync(
                    context.EngineeringContextId,
                    path,
                    revision,
                    cancellationToken);
                return JsonSerializer.Serialize(result, JsonOptions);
            }

            if (name == "legend_prepare_software_repair")
            {
                var expectedBaseSha = LegendEngineeringPolicies.ResolveRepairBaseSha(item);
                var proposalElement = document.RootElement.Clone();
                var proposal = ParseProposal(proposalElement, expectedBaseSha);
                if (proposal is null)
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "engineering_repair_arguments_invalid",
                        expectedBaseSha
                    }, JsonOptions);

                var result = await orchestrator.PrepareRepairAsync(
                    context.EngineeringContextId,
                    proposal,
                    cancellationToken);
                return JsonSerializer.Serialize(result, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "engineering_tool_not_implemented_for_context"
            }, JsonOptions);
        }
    }

    private static string? ReadResponseOutputText(JsonElement output)
    {
        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "type"), "message", StringComparison.Ordinal) ||
                !item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!string.Equals(ReadString(part, "type"), "output_text", StringComparison.Ordinal))
                    continue;
                var text = ReadString(part, "text");
                if (!string.IsNullOrEmpty(text))
                    builder.Append(text);
            }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private async Task<PlanRun> RunOnceAsync(
        string accessToken,
        string model,
        string tier,
        string role,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:TurnTimeoutSeconds") ?? 900,
            30,
            1800)));

        var payload = new
        {
            model,
            instructions = Instruction,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = prompt
                }
            },
            reasoning = new { effort = ResolveEffort(tier) },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "legend_engineering_outcome",
                    strict = true,
                    schema = RoleOutputSchema(role)
                }
            },
            store = false,
            stream = true
        };

        var client = httpClientFactory.CreateClient("LegendChatGptPlanInference");
        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var providerAttempted = false;
        try
        {
            providerAttempted = true;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
            var requestId = ProviderRequestId(response);
            var retryAfter = RetryAfterUtc(response);

            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBoundedBodyAsync(response, 128 * 1024, deadline.Token);
                var error = ReadProviderError(body);
                return PlanRun.Fail(
                    MapHttpFailure(response.StatusCode, error.Code),
                    providerAttempted: true,
                    providerOutcome: "HTTP_REJECTED",
                    httpStatus: (int)response.StatusCode,
                    providerErrorShape: error.Shape,
                    providerErrorCode: error.Code,
                    providerErrorParam: error.Param,
                    providerRequestId: requestId,
                    retryAfterUtc: retryAfter);
            }

            await using var input =
                await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(input);
            var output = new StringBuilder();
            string? responseId = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line is null)
                    return PlanRun.Fail(
                        "chatgpt_plan_response_stream_closed",
                        responseId,
                        providerAttempted: true,
                        providerOutcome: responseId is null ? "STREAM_CLOSED" : "OUTCOME_UNKNOWN",
                        httpStatus: (int)response.StatusCode,
                        providerRequestId: requestId,
                        retryAfterUtc: retryAfter);
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var data = line[5..].TrimStart();
                if (data.Length == 0 || data == "[DONE]")
                    continue;
                if (data.Length > 1_000_000)
                    return PlanRun.Fail(
                        "chatgpt_plan_response_event_too_large",
                        responseId,
                        providerAttempted: true,
                        providerOutcome: "STREAM_FAILED",
                        httpStatus: (int)response.StatusCode,
                        providerRequestId: requestId);

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    var type = ReadString(root, "type");
                    if (root.TryGetProperty("response", out var responseObject) &&
                        responseObject.ValueKind == JsonValueKind.Object)
                        responseId = ReadString(responseObject, "id") ?? responseId;

                    if (type == "response.created")
                        continue;

                    if (type == "response.output_text.delta")
                    {
                        var delta = ReadString(root, "delta");
                        if (delta is not null)
                        {
                            if (output.Length + delta.Length > 256_000)
                                return PlanRun.Fail(
                                    "chatgpt_plan_response_output_too_large",
                                    responseId,
                                    providerAttempted: true,
                                    providerOutcome: "STREAM_FAILED",
                                    httpStatus: (int)response.StatusCode,
                                    providerRequestId: requestId);
                            output.Append(delta);
                        }
                        continue;
                    }

                    if (type == "response.output_text.done" &&
                        output.Length == 0)
                    {
                        var text = ReadString(root, "text");
                        if (text is not null)
                        {
                            if (text.Length > 256_000)
                                return PlanRun.Fail(
                                    "chatgpt_plan_response_output_too_large",
                                    responseId,
                                    providerAttempted: true,
                                    providerOutcome: "STREAM_FAILED",
                                    httpStatus: (int)response.StatusCode,
                                    providerRequestId: requestId);
                            output.Append(text);
                        }
                        continue;
                    }

                    if (type is "response.failed" or "response.incomplete")
                    {
                        var streamError = ReadStreamError(root);
                        return PlanRun.Fail(
                            streamError.Code ?? (type == "response.incomplete"
                                ? "chatgpt_plan_response_incomplete"
                                : "chatgpt_plan_response_failed"),
                            responseId,
                            providerAttempted: true,
                            providerOutcome: type == "response.incomplete"
                                ? "INCOMPLETE"
                                : "FAILED",
                            httpStatus: (int)response.StatusCode,
                            providerErrorShape: streamError.Shape,
                            providerErrorCode: streamError.Code,
                            providerErrorParam: streamError.Param,
                            providerRequestId: requestId,
                            retryAfterUtc: retryAfter);
                    }

                    if (type != "response.completed" ||
                        !root.TryGetProperty("response", out var completed))
                        continue;

                    responseId = ReadString(completed, "id") ?? responseId;
                    var parsed = ParseJson(output.ToString());
                    if (parsed is null)
                        return PlanRun.Fail(
                            "chatgpt_plan_response_output_invalid",
                            responseId,
                            providerAttempted: true,
                            logicalAttemptCompleted: true,
                            providerOutcome: "COMPLETED_INVALID_OUTPUT",
                            httpStatus: (int)response.StatusCode,
                            providerRequestId: requestId,
                            totalTokens: ReadTokens(completed));
                    if (string.IsNullOrWhiteSpace(responseId))
                        responseId = StableId(parsed.Value);
                    return new(
                        true,
                        "completed",
                        responseId,
                        parsed,
                        ReadTokens(completed),
                        true,
                        true,
                        "COMPLETED",
                        (int)response.StatusCode,
                        null,
                        null,
                        null,
                        requestId,
                        retryAfter);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PlanRun.Fail(
                "chatgpt_plan_response_timeout",
                providerAttempted: providerAttempted,
                providerOutcome: providerAttempted ? "OUTCOME_UNKNOWN" : "NOT_SENT");
        }
        catch
        {
            return PlanRun.Fail(
                "chatgpt_plan_response_execution_failed_closed",
                providerAttempted: providerAttempted,
                providerOutcome: providerAttempted ? "OUTCOME_UNKNOWN" : "NOT_SENT");
        }
    }

    private static FounderSoftwareRepairProposal? ParseProposal(
        JsonElement output,
        string expectedBaseSha)
    {
        var baseSha = ReadString(output, "base_sha") ?? expectedBaseSha;
        var title = ReadString(output, "title");
        var summary = ReadString(output, "summary");
        if (!string.Equals(baseSha, expectedBaseSha, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(summary) ||
            !output.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() is < 1 or > 6)
            return null;

        var result = new List<FounderSoftwareRepairChange>();
        foreach (var item in changes.EnumerateArray())
        {
            var path = ReadString(item, "path");
            var source = ReadString(item, "content");
            if (string.IsNullOrWhiteSpace(path) || source is null)
                return null;
            result.Add(new(path, source));
        }

        return new(baseSha, title, summary, result);
    }

    private static object RoleOutputSchema(string role) => role switch
    {
        EngineeringRole.TriageWorker => new
        {
            type = "object",
            properties = new
            {
                decision = new
                {
                    type = "string",
                    @enum = new[] { "ESCALATE_TO_HEAD_GPT", "STOP", "ESCALATE" }
                },
                likely_domain = new { type = "string", maxLength = 160 },
                strong_reasoning_required = new { type = "boolean" },
                summary = new { type = "string", maxLength = 1000 }
            },
            required = new[]
            {
                "decision", "likely_domain", "strong_reasoning_required", "summary"
            },
            additionalProperties = false
        },
        EngineeringRole.HeadGpt => new
        {
            type = "object",
            properties = new
            {
                decision = new
                {
                    type = "string",
                    @enum = new[] { "PROCEED_TO_CODEX", "STOP", "ESCALATE" }
                },
                evidence_sufficient = new { type = "boolean" },
                summary = new { type = "string" }
            },
            required = new[] { "decision", "evidence_sufficient", "summary" },
            additionalProperties = false
        },
        EngineeringRole.IndependentReviewer => new
        {
            type = "object",
            properties = new
            {
                decision = new
                {
                    type = "string",
                    @enum = new[] { "APPROVE_VALIDATION", "REJECT", "ESCALATE" }
                },
                findings = new { type = "array", items = new { type = "string" } },
                summary = new { type = "string" }
            },
            required = new[] { "decision", "findings", "summary" },
            additionalProperties = false
        },
        _ => new
        {
            type = "object",
            properties = new
            {
                decision = new
                {
                    type = "string",
                    @enum = new[] { "REPAIR", "REPAIR_PREPARED", "STOP", "ESCALATE" }
                },
                base_sha = new { type = "string" },
                title = new { type = "string" },
                summary = new { type = "string" },
                changes = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string" },
                            content = new { type = "string" }
                        },
                        required = new[] { "path", "content" },
                        additionalProperties = false
                    }
                }
            },
            required = new[]
            {
                "decision", "base_sha", "title", "summary", "changes"
            },
            additionalProperties = false
        }
    };

    private static EngineeringWorkItemSnapshot ClearLease(
        EngineeringWorkItemSnapshot item) =>
        item with
        {
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null
        };

    private static string ResolveEffort(string tier) =>
        tier is EngineeringModelTier.DeepReasoning or EngineeringModelTier.IndependentReview
            ? "high"
            : tier == EngineeringModelTier.FastTriage
                ? "low"
                : "medium";

    private static object Outcome(
        EngineeringContextSnapshot context,
        string responseId,
        string state) =>
        new
        {
            ok = true,
            provider = ProviderName,
            billingAuthority = "chatgpt_plan_only",
            apiKeyFallback = false,
            agentsApiFallback = false,
            contextId = context.EngineeringContextId,
            context.Role,
            responseId,
            workItemState = state
        };

    private static object Failure(string code) =>
        new
        {
            ok = false,
            error = code,
            provider = ProviderName,
            billingAuthority = "chatgpt_plan_only",
            apiKeyFallback = false,
            agentsApiFallback = false
        };

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            return string.Empty;

        await using var input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > maximumBytes)
                return string.Empty;
            bytes.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string MapHttpFailure(
        System.Net.HttpStatusCode status,
        string? providerCode)
    {
        if (!string.IsNullOrWhiteSpace(providerCode))
            return providerCode!;
        return (int)status switch
        {
            401 => "chatgpt_plan_reauthorization_required",
            403 => "chatgpt_plan_admission_forbidden",
            408 or 429 => "chatgpt_plan_response_temporarily_unavailable",
            >= 500 => "chatgpt_plan_response_temporarily_unavailable",
            _ => "chatgpt_plan_response_rejected"
        };
    }

    private static (string? Code, string? Param, string Shape) ReadProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                    return (
                        ReadString(error, "code") ?? ReadString(error, "type"),
                        ReadString(error, "param"),
                        "STRUCTURED_ERROR");
                if (error.ValueKind == JsonValueKind.String)
                    return (error.GetString(), null, "ROOT_ERROR_STRING");
            }
            if (root.TryGetProperty("detail", out var detail) &&
                detail.ValueKind is JsonValueKind.String or JsonValueKind.Object)
                return (null, null, "DIRECT_DETAIL");
            return (null, null, "JSON_OTHER");
        }
        catch
        {
            return (null, null, "UNPARSEABLE");
        }
    }

    private static (string? Code, string? Param, string Shape) ReadStreamError(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
            return (
                ReadString(error, "code") ?? ReadString(error, "type"),
                ReadString(error, "param"),
                "STREAM_RESPONSE_ERROR");
        if (root.TryGetProperty("error", out var eventError) &&
            eventError.ValueKind == JsonValueKind.Object)
            return (
                ReadString(eventError, "code") ?? ReadString(eventError, "type"),
                ReadString(eventError, "param"),
                "STREAM_EVENT_ERROR");
        return (null, null, "STREAM_TERMINAL_NO_ERROR");
    }

    private static string? ProviderRequestId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-request-id", out var values))
            return null;
        var value = values.FirstOrDefault()?.Trim();
        return value is { Length: > 0 and <= 160 } &&
               value.All(character => char.IsAsciiLetterOrDigit(character) ||
                                      character is '.' or '-' or '_')
            ? value
            : null;
    }

    private static DateTime? RetryAfterUtc(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date)
            return date.UtcDateTime;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
            return DateTime.UtcNow.Add(delta);
        return null;
    }

    private static bool ValidModelSlug(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LegendEngineeringContractAuthority.MaximumModelSlugCharacters &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-' or '_' or '/' or ':');

    private static JsonElement? ParseJson(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw.Trim());
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadTokens(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "total_tokens", "totalTokens" })
            if (usage.TryGetProperty(name, out var value) &&
                value.TryGetInt64(out var count))
                return count;
        return null;
    }

    private static string StableId(JsonElement output)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(output.GetRawText()));
        return "resp_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record PlanRun(
        bool Success,
        string Code,
        string? ResponseId,
        JsonElement? Output,
        long? TotalTokens,
        bool ProviderAttempted,
        bool LogicalAttemptCompleted,
        string ProviderOutcome,
        int? HttpStatus,
        string? ProviderErrorShape,
        string? ProviderErrorCode,
        string? ProviderErrorParam,
        string? ProviderRequestId,
        DateTime? RetryAfterUtc)
    {
        internal static PlanRun Fail(
            string code,
            string? responseId = null,
            bool providerAttempted = false,
            bool logicalAttemptCompleted = false,
            string providerOutcome = "FAILED",
            int? httpStatus = null,
            string? providerErrorShape = null,
            string? providerErrorCode = null,
            string? providerErrorParam = null,
            string? providerRequestId = null,
            DateTime? retryAfterUtc = null,
            long? totalTokens = null) =>
            new(false, code, responseId, null, totalTokens, providerAttempted,
                logicalAttemptCompleted, providerOutcome, httpStatus,
                providerErrorShape, providerErrorCode, providerErrorParam, providerRequestId, retryAfterUtc);
    }
}
