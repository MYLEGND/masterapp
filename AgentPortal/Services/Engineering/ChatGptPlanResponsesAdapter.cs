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
        "Return only JSON matching the supplied role schema. If evidence is insufficient, STOP or ESCALATE. " +
        "For a repair, return complete replacement contents only for files supplied in the bundle. " +
        "Never bypass CI, release authority, or live proof.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);
        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var catalog = credential.Ready && !string.IsNullOrWhiteSpace(credential.AccessToken)
            ? await GetModelCatalogAsync(credential, cancellationToken)
            : new EngineeringModelCatalog(false, credential.Code, []);

        var signature = ReadinessSignature(credential.ClientId, contract);
        var readinessMatches =
            string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) &&
            string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase);
        var head = ResolveReadyModel(EngineeringRole.HeadGpt, credential, catalog);
        var codex = ResolveReadyModel(EngineeringRole.CodexImplementer, credential, catalog);
        var reviewer = ResolveReadyModel(EngineeringRole.IndependentReviewer, credential, catalog);
        var modelsReady = catalog.Ready && readinessMatches &&
                          head is not null && codex is not null && reviewer is not null;
        var circuitOpen = !string.IsNullOrWhiteSpace(credential.ProviderBlockerCode);
        var runtimeReady = credential.Ready && modelsReady && !circuitOpen;
        var eligibility = !contract.ModelExecutionEnabled
            ? "engineering_operational_execution_paused"
            : !credential.Ready
                ? credential.Code
                : circuitOpen
                    ? credential.ProviderBlockerCode!
                    : !catalog.Ready
                        ? catalog.Code
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
            modelCatalogReady = catalog.Ready,
            modelCatalogCode = catalog.Code,
            modelCount = catalog.Models.Count,
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
        var catalog = await GetModelCatalogAsync(credential, cancellationToken);
        if (!catalog.Ready)
            return Failure(catalog.Code);

        var now = DateTime.UtcNow;
        if (!force && !string.IsNullOrWhiteSpace(credential.ProviderBlockerCode) &&
            (credential.ProviderRetryNotBeforeUtc is null ||
             credential.ProviderRetryNotBeforeUtc > now))
            return Failure(credential.ProviderBlockerCode!);

        if (!force &&
            string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) &&
            string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase) &&
            ResolveReadyModel(EngineeringRole.HeadGpt, credential, catalog) is not null &&
            ResolveReadyModel(EngineeringRole.CodexImplementer, credential, catalog) is not null &&
            ResolveReadyModel(EngineeringRole.IndependentReviewer, credential, catalog) is not null)
            return await GetStatusAsync(cancellationToken);

        var providerLease = await credentials.TryAcquireProviderExecutionLeaseAsync(
            "readiness:" + signature[..16],
            TimeSpan.FromMinutes(3),
            allowCircuitProbe: true,
            cancellationToken);
        if (!providerLease.Acquired || string.IsNullOrWhiteSpace(providerLease.LeaseIdentity))
            return Failure(providerLease.Code);

        try
        {
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
                    ? catalog.Models.OrderBy(model => model.Slug, StringComparer.Ordinal).Take(8).Select(model => model.Slug).ToArray()
                    : catalog.Models.Where(model => string.Equals(model.Slug, binding.Configured, StringComparison.Ordinal))
                        .Select(model => model.Slug).ToArray();

                if (candidates.Length == 0)
                {
                    var missing = new ChatGptPlanProviderFailure(
                        "MODEL_BINDING",
                        "chatgpt_plan_model_binding_unavailable",
                        null,
                        null,
                        null,
                        null);
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

                    if (auto && IsUnsupportedCapability(run))
                        continue;

                    var providerFailure = ClassifyProviderFailure(run, credential.ProviderFailureStreak + 1, Guid.Empty);
                    await credentials.RecordProviderFailureAsync(providerFailure, cancellationToken);
                    return Failure(run.Code);
                }

                if (selected is null)
                {
                    var unsupported = new ChatGptPlanProviderFailure(
                        "MODEL_BINDING",
                        "subscription_sharing_unsupported_capability",
                        null,
                        null,
                        400,
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

            var catalog = await GetModelCatalogAsync(credential, cancellationToken);
            if (!catalog.Ready)
                return Failure(catalog.Code);

            var signature = ReadinessSignature(credential.ClientId, operationalContract);
            if (!string.Equals(credential.ReadinessState, "READY", StringComparison.Ordinal) ||
                !string.Equals(credential.ReadinessSignature, signature, StringComparison.OrdinalIgnoreCase))
                return Failure("chatgpt_plan_readiness_canary_required");

            var model = ResolveReadyModel(context.Role, credential, catalog);
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

            var run = await RunOnceAsync(
                credential.AccessToken,
                model,
                item.ModelTier,
                context.Role,
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

            return await ApplyOutcomeAsync(
                context,
                item,
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
            var body = await ReadBoundedBodyAsync(response, 1024 * 1024, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, "chatgpt_plan_model_catalog_unavailable", []);

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
                return new(false, "chatgpt_plan_model_catalog_invalid", []);

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
                ? new(false, "chatgpt_plan_model_catalog_empty", [])
                : new(true, "chatgpt_plan_model_catalog_ready", values);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, "chatgpt_plan_model_catalog_unavailable", []);
        }
    }

    private static string? ResolveModel(
        string configured,
        EngineeringModelCatalog catalog)
    {
        if (!catalog.Ready || catalog.Models.Count == 0)
            return null;
        if (string.Equals(
                configured,
                LegendEngineeringContractAuthority.AutoModel,
                StringComparison.OrdinalIgnoreCase))
            return catalog.Models[0].Slug;

        return catalog.Models.Any(model =>
            string.Equals(model.Slug, configured, StringComparison.Ordinal))
            ? configured
            : null;
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
                    @enum = new[] { "REPAIR", "STOP", "ESCALATE" }
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
            403 => "chatgpt_plan_authorization_context_required",
            408 or 429 => "chatgpt_plan_response_temporarily_unavailable",
            >= 500 => "chatgpt_plan_response_temporarily_unavailable",
            _ => "chatgpt_plan_response_rejected"
        };
    }

    private static (string? Code, string? Param) ReadProviderError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
                return (ReadString(error, "code") ?? ReadString(error, "type"),
                    ReadString(error, "param"));
            return (ReadString(root, "error"), null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? Code, string? Param) ReadStreamError(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
            return (ReadString(error, "code") ?? ReadString(error, "type"),
                ReadString(error, "param"));
        return (null, null);
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
            string? providerErrorCode = null,
            string? providerErrorParam = null,
            string? providerRequestId = null,
            DateTime? retryAfterUtc = null,
            long? totalTokens = null) =>
            new(false, code, responseId, null, totalTokens, providerAttempted,
                logicalAttemptCompleted, providerOutcome, httpStatus,
                providerErrorCode, providerErrorParam, providerRequestId, retryAfterUtc);
    }
}
