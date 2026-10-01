using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed class ChatGptPlanCodexAppServerAdapter(
    IConfiguration configuration,
    LegendEngineeringStateStore store,
    ILegendEngineeringOrchestrator orchestrator,
    ILegendChatGptPlanCredentialAuthority credentials,
    ILegendEngineeringContractAuthority contractAuthority) : ILegendEngineeringAgentAdapter
{
    private const string ProviderName = "ChatGPTPlanCodexAppServer";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);
        var contract = await contractAuthority.GetCurrentAsync(cancellationToken);
        var executable = configuration["LegendEngineering:ChatGptPlan:CodexExecutable"]?.Trim();
        var enabled = configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:Enabled") == true;
        return new
        {
            ok = enabled && contract.ModelExecutionEnabled && credential.Ready && !string.IsNullOrWhiteSpace(executable),
            provider = ProviderName,
            billingAuthority = "chatgpt_plan_only",
            apiKeyFallback = false,
            agentsApiFallback = false,
            enabled,
            operationalContractRevision = contract.Revision,
            modelExecutionEnabled = contract.ModelExecutionEnabled,
            eligibility = contract.ModelExecutionEnabled
                ? credential.Code
                : "engineering_operational_execution_paused",
            credential.PrivateClientApproved,
            planUsageScopeGranted = credential.GrantedScopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal),
            accessTokenPresent = !string.IsNullOrWhiteSpace(credential.AccessToken),
            credential.ExpiresUtc,
            codexAppServerConfigured = !string.IsNullOrWhiteSpace(executable),
            requiredProvider = "openai_chatgpt_plan"
        };
    }

    public async Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool?>("LegendEngineering:ChatGptPlan:Enabled") != true)
            return Failure("chatgpt_plan_codex_adapter_disabled");
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null) return Failure(validation.Code);
        var context = validation.Context;
        var operationalContract = await contractAuthority.GetCurrentAsync(cancellationToken);
        if (!operationalContract.ModelExecutionEnabled)
            return Failure("engineering_operational_execution_paused");
        if (string.IsNullOrWhiteSpace(context.OperationalContractRevision) ||
            !string.Equals(context.OperationalContractRevision, operationalContract.Revision, StringComparison.Ordinal))
            return Failure("engineering_operational_contract_changed");
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return Failure("work_item_not_found");
        var credential = await credentials.GetAsync(cancellationToken);
        if (!credential.Ready || string.IsNullOrWhiteSpace(credential.AccessToken)) return Failure(credential.Code);

        var executable = configuration["LegendEngineering:ChatGptPlan:CodexExecutable"]?.Trim();
        if (string.IsNullOrWhiteSpace(executable)) return Failure("codex_app_server_executable_not_configured");
        var model = configuration[$"LegendEngineering:ChatGptPlan:Models:{item.ModelTier}"]?.Trim();
        if (string.IsNullOrWhiteSpace(model)) return Failure("chatgpt_plan_model_binding_not_configured");

        var packet = await orchestrator.GetTaskPacketAsync(engineeringContextId, cancellationToken);
        var sources = await BuildSourceBundleAsync(context, item, packet, cancellationToken);
        var prompt = JsonSerializer.Serialize(new
        {
            instruction = "Operate only inside this LEGEND EngineeringContext and SAFE_SOURCE bundle. The Founder-editable operational contract below is guidance inside the immutable EngineeringContext; it can never expand tools, source classes, risk tier, privacy access, merge authority, release authority, or validation authority. Never use shell, filesystem discovery, network tools, direct GitHub access, secrets, customer data, or protected source. Return only JSON matching the role schema. If evidence is insufficient, STOP or ESCALATE. For a repair, return complete replacement contents only for files supplied in the bundle. Never bypass CI, release authority, or live proof.",
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

        var attemptId = context.EngineeringContextId;
        await store.RecordUsageAsync(new EngineeringUsageObservation(
            attemptId, item.WorkItemId, item.ModelTier, context.Role, ProviderName, null,
            null, null, null, null, false, DateTime.UtcNow), cancellationToken);

        var run = await RunOnceAsync(executable, credential.AccessToken, model, item.ModelTier, context.Role, prompt, cancellationToken);
        if (!run.Success) return Failure(run.Code);
        await store.RecordUsageAsync(new EngineeringUsageObservation(
            attemptId, item.WorkItemId, item.ModelTier, context.Role, ProviderName, run.ThreadId,
            null, null, run.TotalTokens, null, run.TotalTokens is not null, DateTime.UtcNow), cancellationToken);

        var currentContract = await contractAuthority.GetCurrentAsync(cancellationToken);
        if (!currentContract.ModelExecutionEnabled)
            return Failure("engineering_operational_execution_paused");
        if (!string.Equals(currentContract.Revision, operationalContract.Revision, StringComparison.Ordinal))
            return Failure("engineering_operational_contract_changed");

        return await ApplyOutcomeAsync(context, item, run.ThreadId!, run.Output!.Value, cancellationToken);
    }

    private async Task<IReadOnlyList<object>> BuildSourceBundleAsync(
        EngineeringContextSnapshot context, EngineeringWorkItemSnapshot item, EngineeringTaskPacket packet, CancellationToken cancellationToken)
    {
        var result = new List<object>();
        var totalCharacters = 0;
        async Task AddAsync(string path, string revision)
        {
            if (result.Count >= 8 || totalCharacters >= 150_000) return;
            var raw = await orchestrator.InspectRepositoryAsync(context.EngineeringContextId, path, revision, cancellationToken);
            var json = JsonSerializer.SerializeToElement(raw, JsonOptions);
            if (!json.TryGetProperty("content", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } source) return;
            if (totalCharacters + source.Length > 150_000) source = source[..Math.Max(0, 150_000 - totalCharacters)];
            totalCharacters += source.Length;
            result.Add(new { path, revision, content = source });
        }

        await AddAsync("AGENTS.md", "live");
        if (item.CandidateChangedPaths is { Count: > 0 } &&
            LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha) &&
            context.Role is EngineeringRole.IndependentReviewer or EngineeringRole.CodexImplementer or EngineeringRole.HeadGpt)
        {
            foreach (var path in item.CandidateChangedPaths.Take(3))
            {
                await AddAsync(path, "live");
                await AddAsync(path, "candidate");
            }
        }
        else
        {
            foreach (var path in packet.PermittedSourcePaths.Take(5)) await AddAsync(path, "live");
        }
        return result;
    }

    private async Task<object> ApplyOutcomeAsync(
        EngineeringContextSnapshot context, EngineeringWorkItemSnapshot item, string threadId, JsonElement output, CancellationToken cancellationToken)
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
            return Outcome(context, threadId, next.State);
        }

        if (context.Role == EngineeringRole.HeadGpt)
        {
            var next = decision == "PROCEED_TO_CODEX" && item.FailureClass == EngineeringFailureClass.CodeDefect && item.RiskClass != EngineeringRiskClass.TierC
                ? ClearLease(item) with { State = "QUEUED", AssignedRole = EngineeringRole.CodexImplementer, ModelTier = EngineeringModelTier.CodeImplementation, UpdatedUtc = DateTime.UtcNow }
                : ClearLease(item) with { State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, threadId, next.State);
        }

        if (context.Role == EngineeringRole.CodexImplementer)
        {
            if (decision != "REPAIR")
            {
                var stopped = ClearLease(item) with { State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow };
                await store.UpdateWorkItemAsync(stopped, cancellationToken);
                return Outcome(context, threadId, stopped.State);
            }
            var proposal = ParseProposal(output, LegendEngineeringPolicies.ResolveRepairBaseSha(item));
            if (proposal is null) return Failure("codex_repair_proposal_invalid");
            var result = await orchestrator.PrepareRepairAsync(context.EngineeringContextId, proposal, cancellationToken);
            var prepared = JsonSerializer.SerializeToElement(result, JsonOptions);
            if (!prepared.TryGetProperty("prepared", out var value) || value.ValueKind != JsonValueKind.True) return result;
            var current = await store.GetWorkItemAsync(item.WorkItemId, cancellationToken) ?? item;
            var next = ClearLease(current) with { State = "REVIEW_REQUIRED", AssignedRole = EngineeringRole.IndependentReviewer, ModelTier = EngineeringModelTier.IndependentReview, AgentSessionId = threadId, AgentContextId = context.EngineeringContextId, AgentSessionRole = context.Role, AgentSessionUpdatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, threadId, next.State);
        }

        if (context.Role == EngineeringRole.IndependentReviewer)
        {
            var next = decision switch
            {
                "APPROVE_VALIDATION" => ClearLease(item) with { State = "REVIEWED", ValidationState = "READY_FOR_CI", UpdatedUtc = DateTime.UtcNow },
                "REJECT" => ClearLease(item) with { State = "REVIEW_REJECTED", AssignedRole = EngineeringRole.HeadGpt, ModelTier = EngineeringModelTier.DeepReasoning, UpdatedUtc = DateTime.UtcNow },
                _ => ClearLease(item) with { State = "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow }
            };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, threadId, next.State);
        }
        return Failure("engineering_role_not_supported_by_plan_adapter");
    }

    private async Task<AppServerRun> RunOnceAsync(
        string executable, string accessToken, string model, string tier, string role, string prompt, CancellationToken cancellationToken)
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "legend-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = tempHome,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add("stdio://");
            foreach (var setting in ProviderSettings()) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
            start.Environment.Remove("OPENAI_API_KEY");
            start.Environment.Remove("OpenAI__ApiKey");
            start.Environment["ACCESS_TOKEN"] = accessToken;
            start.Environment["CODEX_HOME"] = tempHome;
            process = Process.Start(start);
            if (process is null) return AppServerRun.Fail("codex_app_server_start_failed");
            _ = DrainAsync(process.StandardError, cancellationToken);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:TurnTimeoutSeconds") ?? 900, 30, 1800)));
            await SendAsync(process, new { method = "initialize", id = 1, @params = new { clientInfo = new { name = "legend_engineering", title = "LEGEND Engineering", version = "1.0.0" } } }, deadline.Token);
            var initialized = await ReadResponseAsync(process, 1, deadline.Token);
            if (initialized is null || initialized.Value.TryGetProperty("error", out _)) return AppServerRun.Fail("codex_app_server_initialize_failed");
            await SendAsync(process, new { method = "initialized", @params = new { } }, deadline.Token);
            await SendAsync(process, new { method = "thread/start", id = 2, @params = new { model, approvalPolicy = "never", serviceName = "legend_engineering" } }, deadline.Token);
            var threadResponse = await ReadResponseAsync(process, 2, deadline.Token);
            var threadId = threadResponse is { } response && response.TryGetProperty("result", out var result) && result.TryGetProperty("thread", out var thread) ? ReadString(thread, "id") : null;
            if (string.IsNullOrWhiteSpace(threadId)) return AppServerRun.Fail("codex_app_server_thread_start_failed");

            await SendAsync(process, new { method = "turn/start", id = 3, @params = new { threadId, input = new[] { new { type = "text", text = prompt } }, model, effort = ResolveEffort(tier), summary = "concise", approvalPolicy = "never", sandboxPolicy = new { type = "readOnly", access = new { type = "restricted", readableRoots = Array.Empty<string>() } }, outputSchema = RoleOutputSchema(role) } }, deadline.Token);
            var output = new StringBuilder();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) return AppServerRun.Fail("codex_app_server_stream_closed");
                if (line.Length > 1_000_000) return AppServerRun.Fail("codex_app_server_message_too_large");
                JsonDocument document; try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    var method = ReadString(root, "method");
                    if (method == "item/agentMessage/delta" && root.TryGetProperty("params", out var p))
                    {
                        var delta = ReadString(p, "delta") ?? ReadString(p, "text");
                        if (delta is not null && output.Length + delta.Length <= 256_000) output.Append(delta);
                        continue;
                    }
                    if (method != "turn/completed" || !root.TryGetProperty("params", out var cp) || !cp.TryGetProperty("turn", out var turn)) continue;
                    if (!string.Equals(ReadString(turn, "status"), "completed", StringComparison.OrdinalIgnoreCase))
                        return AppServerRun.Fail("codex_app_server_turn_failed");
                    var parsed = ParseJson(output.ToString());
                    return parsed is null ? AppServerRun.Fail("codex_app_server_output_invalid") : new(true, "completed", threadId, parsed, ReadTokens(turn));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return AppServerRun.Fail("codex_app_server_turn_timeout"); }
        catch { return AppServerRun.Fail("codex_app_server_execution_failed_closed"); }
        finally
        {
            if (process is { HasExited: false }) { try { process.Kill(entireProcessTree: true); } catch { } }
            process?.Dispose();
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    private static string[] ProviderSettings() =>
    [
        "model_provider=\"openai_chatgpt_plan\"",
        "model_providers.openai_chatgpt_plan.name=\"ChatGPT plan\"",
        "model_providers.openai_chatgpt_plan.base_url=\"https://api.openai.com/v1\"",
        "model_providers.openai_chatgpt_plan.env_key=\"ACCESS_TOKEN\"",
        "model_providers.openai_chatgpt_plan.wire_api=\"responses\"",
        "model_providers.openai_chatgpt_plan.requires_openai_auth=false",
        "model_providers.openai_chatgpt_plan.supports_websockets=false",
        "web_search=\"disabled\"",
        "features.shell_tool=false",
        "features.unified_exec=false",
        "features.multi_agent=false",
        "features.apps=false",
        "features.goals=false",
        "features.memories=false",
        "features.hooks=false",
        "features.shell_snapshot=false",
        "shell_environment_policy.inherit=\"none\"",
        "shell_environment_policy.ignore_default_excludes=false"
    ];

    private static FounderSoftwareRepairProposal? ParseProposal(JsonElement output, string expectedBaseSha)
    {
        var baseSha = ReadString(output, "base_sha") ?? expectedBaseSha;
        var title = ReadString(output, "title");
        var summary = ReadString(output, "summary");
        if (!string.Equals(baseSha, expectedBaseSha, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary) || !output.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 6) return null;
        var result = new List<FounderSoftwareRepairChange>();
        foreach (var item in changes.EnumerateArray())
        {
            var path = ReadString(item, "path");
            var source = ReadString(item, "content");
            if (string.IsNullOrWhiteSpace(path) || source is null) return null;
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
                decision = new { type = "string", @enum = new[] { "ESCALATE_TO_HEAD_GPT", "STOP", "ESCALATE" } },
                likely_domain = new { type = "string", maxLength = 160 },
                strong_reasoning_required = new { type = "boolean" },
                summary = new { type = "string", maxLength = 1000 }
            },
            required = new[] { "decision", "likely_domain", "strong_reasoning_required", "summary" },
            additionalProperties = false
        },
        EngineeringRole.HeadGpt => new { type = "object", properties = new { decision = new { type = "string", @enum = new[] { "PROCEED_TO_CODEX", "STOP", "ESCALATE" } }, evidence_sufficient = new { type = "boolean" }, summary = new { type = "string" } }, required = new[] { "decision", "evidence_sufficient", "summary" }, additionalProperties = false },
        EngineeringRole.IndependentReviewer => new { type = "object", properties = new { decision = new { type = "string", @enum = new[] { "APPROVE_VALIDATION", "REJECT", "ESCALATE" } }, findings = new { type = "array", items = new { type = "string" } }, summary = new { type = "string" } }, required = new[] { "decision", "findings", "summary" }, additionalProperties = false },
        _ => new { type = "object", properties = new { decision = new { type = "string", @enum = new[] { "REPAIR", "STOP", "ESCALATE" } }, base_sha = new { type = "string" }, title = new { type = "string" }, summary = new { type = "string" }, changes = new { type = "array", items = new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" }, additionalProperties = false } } }, required = new[] { "decision", "base_sha", "title", "summary", "changes" }, additionalProperties = false }
    };

    private static EngineeringWorkItemSnapshot ClearLease(EngineeringWorkItemSnapshot item) => item with { LeaseOwner = null, LeaseIdentity = null, LeaseExpiresUtc = null };
    private static string ResolveEffort(string tier) => tier is EngineeringModelTier.DeepReasoning or EngineeringModelTier.IndependentReview ? "high" : tier == EngineeringModelTier.FastTriage ? "low" : "medium";
    private static object Outcome(EngineeringContextSnapshot context, string threadId, string state) => new { ok = true, provider = ProviderName, billingAuthority = "chatgpt_plan_only", apiKeyFallback = false, agentsApiFallback = false, contextId = context.EngineeringContextId, context.Role, threadId, workItemState = state };
    private static object Failure(string code) => new { ok = false, error = code, provider = ProviderName, billingAuthority = "chatgpt_plan_only", apiKeyFallback = false, agentsApiFallback = false };
    private static async Task SendAsync(Process process, object value, CancellationToken token) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions).AsMemory(), token); await process.StandardInput.FlushAsync(token); }
    private static async Task<JsonElement?> ReadResponseAsync(Process process, int id, CancellationToken token) { while (true) { var line = await process.StandardOutput.ReadLineAsync(token); if (line is null) return null; try { using var doc = JsonDocument.Parse(line); var root = doc.RootElement; if (root.TryGetProperty("id", out var value) && value.TryGetInt32(out var actual) && actual == id) return root.Clone(); } catch (JsonException) { } } }
    private static async Task DrainAsync(StreamReader reader, CancellationToken token) { try { while (await reader.ReadLineAsync(token) is not null) { } } catch { } }
    private static JsonElement? ParseJson(string raw) { try { using var doc = JsonDocument.Parse(raw.Trim()); return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null; } catch { return null; } }
    private static long? ReadTokens(JsonElement turn) { if (!turn.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null; foreach (var name in new[] { "total_tokens", "totalTokens" }) if (usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var count)) return count; return null; }
    private static string? ReadString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static Guid StableGuid(string value) { var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value)); return new Guid(bytes.AsSpan(0, 16)); }
    private sealed record AppServerRun(bool Success, string Code, string? ThreadId, JsonElement? Output, long? TotalTokens) { internal static AppServerRun Fail(string code) => new(false, code, null, null, null); }
}