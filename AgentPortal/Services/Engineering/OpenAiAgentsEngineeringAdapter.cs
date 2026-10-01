using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Services.Analytics;
using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal interface ILegendEngineeringAgentAdapter
{
    Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken);
    Task<object> AdvanceAsync(Guid engineeringContextId, string sessionId, CancellationToken cancellationToken);
}

internal sealed class OpenAiAgentsEngineeringAdapter(
    IHttpClientFactory clients,
    IConfiguration configuration,
    LegendEngineeringStateStore store,
    ILegendEngineeringOrchestrator orchestrator) : ILegendEngineeringAgentAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> StartAsync(Guid engineeringContextId, CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            return Failure(validation.Code);

        var context = validation.Context;
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return Failure("work_item_not_found");
        if (configuration.GetValue<bool?>("LegendEngineering:OpenAI:Enabled") == false)
            return Failure("engineering_agents_api_disabled");

        var apiKey = OpenAiKeyResolver.Resolve(configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failure("openai_api_key_not_configured");

        var model = ResolveModel(item.ModelTier);
        if (string.IsNullOrWhiteSpace(model))
            return Failure("engineering_model_binding_not_configured");

        var packet = await orchestrator.GetTaskPacketAsync(engineeringContextId, cancellationToken);
        var requestBody = new
        {
            agent = new
            {
                model,
                instructions = BuildInstructions(context),
                reasoning = new { effort = ResolveReasoningEffort(item.ModelTier) },
                tools = BuildTools(context.Role),
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        schema = OutputSchema(context.Role)
                    },
                    verbosity = "low"
                }
            },
            environment = new { type = "none" },
            input = JsonSerializer.Serialize(new
            {
                engineeringContextId = context.EngineeringContextId,
                context.ContractRevision,
                context.PolicyRevision,
                context.WorkItemId,
                context.Role,
                context.FailureClass,
                context.RiskClass,
                context.ComplexityScore,
                context.LiveSha,
                context.EvidenceRevision,
                context.AllowedTools,
                context.AllowedSourceClasses,
                context.ProtectedAreas,
                context.AttemptLimit,
                context.StopConditions,
                taskPacket = packet
            }, JsonOptions),
            metadata = new Dictionary<string, string>
            {
                ["legend_work_item_id"] = context.WorkItemId.ToString("N"),
                ["legend_context_id"] = context.EngineeringContextId.ToString("N"),
                ["legend_role"] = context.Role,
                ["legend_contract_revision"] = context.ContractRevision
            },
            stream = false
        };

        using var request = CreateRequest(HttpMethod.Post, "v1/agents/sessions", apiKey, requestBody);
        using var response = await clients.CreateClient("OpenAI").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created && !response.IsSuccessStatusCode)
            return Failure("agents_api_session_create_failed", (int)response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var session = document.RootElement.Clone();
        var sessionId = ReadString(session, "id");
        var status = ReadString(session, "status");
        if (string.IsNullOrWhiteSpace(sessionId))
            return Failure("agents_api_session_identity_missing");

        var updated = item with
        {
            State = "AGENT_ACTIVE",
            AgentSessionId = sessionId,
            AgentSessionRole = context.Role,
            AgentSessionUpdatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        await store.UpdateWorkItemAsync(updated, cancellationToken);

        if (status == "requires_action")
            return await HandleRequiredActionsAsync(context, sessionId, session, apiKey, cancellationToken);
        if (status is "idle" or "failed")
            return await CompleteTerminalSessionAsync(context, sessionId, session, cancellationToken);

        return new
        {
            ok = true,
            provider = "OpenAIAgentsAPI",
            sessionId,
            status,
            role = context.Role,
            environment = "none",
            arbitraryShell = false,
            directRepositoryAccess = false
        };
    }

    public async Task<object> AdvanceAsync(
        Guid engineeringContextId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSessionId(sessionId)) return Failure("agents_api_session_identity_invalid");
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            return Failure(validation.Code);
        var context = validation.Context;
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return Failure("work_item_not_found");
        if (!string.Equals(item.AgentSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(item.AgentSessionRole, context.Role, StringComparison.Ordinal))
            return Failure("agents_api_session_not_bound_to_work_item");

        var apiKey = OpenAiKeyResolver.Resolve(configuration);
        if (string.IsNullOrWhiteSpace(apiKey)) return Failure("openai_api_key_not_configured");

        using var request = CreateRequest(HttpMethod.Get, "v1/agents/sessions/" + Uri.EscapeDataString(sessionId), apiKey, body: null);
        using var response = await clients.CreateClient("OpenAI").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Failure("agents_api_session_retrieve_failed", (int)response.StatusCode);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var session = document.RootElement.Clone();
        var status = ReadString(session, "status");

        if (status == "requires_action")
            return await HandleRequiredActionsAsync(context, sessionId, session, apiKey, cancellationToken);
        if (status is "idle" or "failed")
            return await CompleteTerminalSessionAsync(context, sessionId, session, cancellationToken);

        return new { ok = true, provider = "OpenAIAgentsAPI", sessionId, status, role = context.Role };
    }

    private async Task<object> HandleRequiredActionsAsync(
        EngineeringContextSnapshot context,
        string sessionId,
        JsonElement session,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!session.TryGetProperty("required_actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return Failure("agents_api_required_actions_invalid");

        var events = new List<object>();
        foreach (var action in actions.EnumerateArray())
        {
            var type = ReadString(action, "type");
            var turnId = ReadString(action, "turn_id");
            var callId = ReadString(action, "call_id");
            var name = ReadString(action, "name");
            if (type != "function_call" || string.IsNullOrWhiteSpace(turnId) ||
                string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
            {
                events.Add(new
                {
                    type = "agent.session.input.tool_result",
                    turn_id = turnId ?? string.Empty,
                    call_id = callId ?? string.Empty,
                    success = false,
                    error = "unsupported_required_action"
                });
                continue;
            }

            var outcome = await ExecuteFunctionAsync(context, name, action, cancellationToken);
            events.Add(outcome.Success
                ? new
                {
                    type = "agent.session.input.tool_result",
                    turn_id = turnId,
                    call_id = callId,
                    success = true,
                    output = outcome.Output
                }
                : new
                {
                    type = "agent.session.input.tool_result",
                    turn_id = turnId,
                    call_id = callId,
                    success = false,
                    error = outcome.Error
                });
        }

        if (events.Count == 0) return Failure("agents_api_required_actions_empty");
        var idempotency = Hash(sessionId + "|" + string.Join("|", actions.EnumerateArray().Select(action => ReadString(action, "call_id") ?? string.Empty)));
        using var request = CreateRequest(
            HttpMethod.Post,
            "v1/agents/sessions/" + Uri.EscapeDataString(sessionId) + "/events",
            apiKey,
            new { events, idempotency_key = idempotency });
        using var response = await clients.CreateClient("OpenAI").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Failure("agents_api_tool_result_submission_uncertain", (int)response.StatusCode);

        return new
        {
            ok = true,
            provider = "OpenAIAgentsAPI",
            sessionId,
            status = "actions_submitted",
            actions = events.Count,
            idempotencyKey = idempotency,
            directRepositoryWrite = false
        };
    }

    private async Task<(bool Success, string Output, string Error)> ExecuteFunctionAsync(
        EngineeringContextSnapshot context,
        string name,
        JsonElement action,
        CancellationToken cancellationToken)
    {
        if (!context.AllowedTools.Contains(name, StringComparer.Ordinal))
            return (false, string.Empty, "engineering_context_tool_not_allowed");
        var args = action.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
            ? arguments
            : default;
        if (args.ValueKind != JsonValueKind.Object)
            return (false, string.Empty, "engineering_tool_arguments_invalid");

        try
        {
            if (name == "legend_inspect_repository")
            {
                var path = ReadString(args, "path");
                var revision = ReadString(args, "revision") ?? "live";
                if (string.IsNullOrWhiteSpace(path) || revision is not ("live" or "candidate"))
                    return (false, string.Empty, "engineering_repository_arguments_invalid");
                var result = await orchestrator.InspectRepositoryAsync(context.EngineeringContextId, path, revision, cancellationToken);
                return (true, JsonSerializer.Serialize(result, JsonOptions), string.Empty);
            }

            if (name == "legend_prepare_software_repair")
            {
                if (context.Role != EngineeringRole.CodexImplementer)
                    return (false, string.Empty, "engineering_role_cannot_prepare_repair");
                var baseSha = ReadString(args, "base_sha");
                var title = ReadString(args, "title");
                var summary = ReadString(args, "summary");
                if (baseSha is null || title is null || summary is null ||
                    !args.TryGetProperty("changes", out var changesElement) || changesElement.ValueKind != JsonValueKind.Array ||
                    changesElement.GetArrayLength() is < 1 or > 6)
                    return (false, string.Empty, "engineering_repair_arguments_invalid");

                var changes = new List<FounderSoftwareRepairChange>();
                foreach (var change in changesElement.EnumerateArray())
                {
                    var path = ReadString(change, "path");
                    var source = ReadString(change, "content");
                    if (string.IsNullOrWhiteSpace(path) || source is null)
                        return (false, string.Empty, "engineering_repair_arguments_invalid");
                    changes.Add(new FounderSoftwareRepairChange(path, source));
                }

                var result = await orchestrator.PrepareRepairAsync(
                    context.EngineeringContextId,
                    new FounderSoftwareRepairProposal(baseSha, title, summary, changes),
                    cancellationToken);
                return (true, JsonSerializer.Serialize(result, JsonOptions), string.Empty);
            }

            return (false, string.Empty, "engineering_tool_not_implemented");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return (false, string.Empty, "engineering_tool_execution_failed_closed");
        }
    }

    private async Task<object> CompleteTerminalSessionAsync(
        EngineeringContextSnapshot context,
        string sessionId,
        JsonElement session,
        CancellationToken cancellationToken)
    {
        var status = ReadString(session, "status") ?? "unknown";
        await RecordTerminalUsageAsync(context, sessionId, session, cancellationToken);
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return Failure("work_item_not_found");

        if (status == "failed")
        {
            var failed = ClearSession(item) with
            {
                State = "FOUNDER_ESCALATION",
                UpdatedUtc = DateTime.UtcNow
            };
            await store.UpdateWorkItemAsync(failed, cancellationToken);
            return new { ok = false, error = "agents_api_session_failed", sessionId, role = context.Role };
        }

        var output = await ReadTerminalJsonAsync(sessionId, cancellationToken);
        if (output is null)
        {
            var stopped = ClearSession(item) with
            {
                State = "FOUNDER_ESCALATION",
                UpdatedUtc = DateTime.UtcNow
            };
            await store.UpdateWorkItemAsync(stopped, cancellationToken);
            return Failure("agents_api_terminal_output_missing");
        }

        EngineeringWorkItemSnapshot next;
        if (context.Role == EngineeringRole.HeadGpt)
        {
            var decision = ReadString(output.Value, "decision");
            next = decision switch
            {
                "PROCEED_TO_CODEX" when item.FailureClass == EngineeringFailureClass.CodeDefect &&
                                        item.RiskClass != EngineeringRiskClass.TierC =>
                    ClearSession(item) with
                    {
                        State = "QUEUED",
                        AssignedRole = EngineeringRole.CodexImplementer,
                        ModelTier = EngineeringModelTier.CodeImplementation,
                        UpdatedUtc = DateTime.UtcNow
                    },
                "STOP" => ClearSession(item) with { State = "STOPPED", UpdatedUtc = DateTime.UtcNow },
                _ => ClearSession(item) with { State = "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow }
            };
        }
        else if (context.Role == EngineeringRole.CodexImplementer)
        {
            item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken) ?? item;
            next = LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha) && item.PullRequestNumber is > 0
                ? ClearSession(item) with
                {
                    State = "REVIEW_REQUIRED",
                    AssignedRole = EngineeringRole.IndependentReviewer,
                    ModelTier = EngineeringModelTier.IndependentReview,
                    UpdatedUtc = DateTime.UtcNow
                }
                : ClearSession(item) with { State = "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow };
        }
        else if (context.Role == EngineeringRole.IndependentReviewer)
        {
            var decision = ReadString(output.Value, "decision");
            next = decision switch
            {
                "APPROVE_VALIDATION" => ClearSession(item) with
                {
                    State = "REVIEWED",
                    ValidationState = "READY_FOR_CI",
                    UpdatedUtc = DateTime.UtcNow
                },
                "REJECT" => ClearSession(item) with
                {
                    State = "REVIEW_REJECTED",
                    AssignedRole = EngineeringRole.HeadGpt,
                    ModelTier = EngineeringModelTier.DeepReasoning,
                    UpdatedUtc = DateTime.UtcNow
                },
                _ => ClearSession(item) with { State = "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow }
            };
        }
        else
        {
            next = ClearSession(item) with { State = "FOUNDER_ESCALATION", UpdatedUtc = DateTime.UtcNow };
        }

        await store.UpdateWorkItemAsync(next, cancellationToken);
        return new
        {
            ok = true,
            provider = "OpenAIAgentsAPI",
            sessionId,
            status,
            role = context.Role,
            workItemState = next.State,
            nextRole = next.AssignedRole
        };
    }

    private async Task<JsonElement?> ReadTerminalJsonAsync(string sessionId, CancellationToken cancellationToken)
    {
        var apiKey = OpenAiKeyResolver.Resolve(configuration);
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        using var request = CreateRequest(HttpMethod.Get,
            "v1/agents/sessions/" + Uri.EscapeDataString(sessionId) + "/items?limit=20&order=desc",
            apiKey, body: null);
        using var response = await clients.CreateClient("OpenAI").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            if (ReadString(item, "role") != "assistant" ||
                !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (ReadString(part, "type") != "output_text") continue;
                var text = ReadString(part, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    using var parsed = JsonDocument.Parse(text);
                    return parsed.RootElement.Clone();
                }
                catch (JsonException) { return null; }
            }
        }
        return null;
    }

    private async Task RecordTerminalUsageAsync(
        EngineeringContextSnapshot context,
        string sessionId,
        JsonElement session,
        CancellationToken cancellationToken)
    {
        long? input = null, output = null, total = null;
        var observed = session.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
        if (observed)
        {
            input = ReadInt64(usage, "input_tokens");
            output = ReadInt64(usage, "output_tokens");
            total = ReadInt64(usage, "total_tokens");
            observed = total is not null;
        }

        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        await store.RecordUsageAsync(new EngineeringUsageObservation(
            StableGuid(sessionId),
            context.WorkItemId,
            item?.ModelTier ?? EngineeringModelTier.StandardReasoning,
            context.Role,
            "OpenAIAgentsAPI",
            sessionId,
            input,
            output,
            total,
            null,
            observed,
            DateTime.UtcNow), cancellationToken);
    }

    private string? ResolveModel(string modelTier)
        => configuration[$"LegendEngineering:OpenAI:Models:{modelTier}"] ??
           configuration["OpenAI:LegendFounderAiModel"];

    private string ResolveReasoningEffort(string modelTier)
        => configuration[$"LegendEngineering:OpenAI:Reasoning:{modelTier}"] ??
           (modelTier switch
           {
               EngineeringModelTier.FastTriage => "low",
               EngineeringModelTier.DeepReasoning => "high",
               EngineeringModelTier.IndependentReview => "high",
               _ => "medium"
           });

    private static string BuildInstructions(EngineeringContextSnapshot context)
        => $"""
        You are operating as {context.Role} inside the LEGEND Founder engineering control plane.
        The server-side EngineeringContext is authoritative. Never broaden your role, source access, risk class, budget, or tools.
        First inspect AGENTS.md through legend_inspect_repository using revision=live.
        Do not request or infer secrets, credentials, private customer data, raw production payloads, or protected source.
        Work only from the exact immutable revision supplied by the context and bounded task packet.
        Repair canonical ownership only; do not add overrides, duplicate authorities, or unrelated changes.
        If evidence is insufficient or a stop condition is reached, stop or escalate rather than guess.
        Return only the required JSON object for your role.
        """;

    private static object[] BuildTools(string role)
    {
        var inspect = new
        {
            type = "function",
            name = "legend_inspect_repository",
            description = "Read one bounded SAFE_SOURCE repository file through LEGEND's canonical remediation authority at the exact live or candidate revision. Protected source remains opaque.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", minLength = 1, maxLength = 260 },
                    revision = new { type = "string", @enum = new[] { "live", "candidate" } }
                },
                required = new[] { "path", "revision" },
                additionalProperties = false
            }
        };
        if (role != EngineeringRole.CodexImplementer) return [inspect];

        var prepare = new
        {
            type = "function",
            name = "legend_prepare_software_repair",
            description = "Prepare one bounded canonical repair through the existing FounderSoftwareRemediationService. This creates only the isolated candidate/PR path; it cannot bypass CI or directly deploy.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    base_sha = new { type = "string", minLength = 40, maxLength = 40 },
                    title = new { type = "string", minLength = 1, maxLength = 160 },
                    summary = new { type = "string", minLength = 1, maxLength = 4000 },
                    changes = new
                    {
                        type = "array", minItems = 1, maxItems = 6,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string", minLength = 1, maxLength = 260 },
                                content = new { type = "string", minLength = 1, maxLength = 60000 }
                            },
                            required = new[] { "path", "content" },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "base_sha", "title", "summary", "changes" },
                additionalProperties = false
            }
        };
        return [inspect, prepare];
    }

    private static object OutputSchema(string role)
    {
        object StringEnum(params string[] values) => new { type = "string", @enum = values };
        return role switch
        {
            EngineeringRole.HeadGpt => new
            {
                type = "object",
                properties = new
                {
                    decision = StringEnum("PROCEED_TO_CODEX", "STOP", "ESCALATE"),
                    evidence_sufficient = new { type = "boolean" },
                    topology = new { type = "array", items = new { type = "string", maxLength = 160 }, maxItems = 12 },
                    summary = new { type = "string", maxLength = 800 }
                },
                required = new[] { "decision", "evidence_sufficient", "topology", "summary" },
                additionalProperties = false
            },
            EngineeringRole.IndependentReviewer => new
            {
                type = "object",
                properties = new
                {
                    decision = StringEnum("APPROVE_VALIDATION", "REJECT", "ESCALATE"),
                    findings = new { type = "array", items = new { type = "string", maxLength = 240 }, maxItems = 12 },
                    summary = new { type = "string", maxLength = 800 }
                },
                required = new[] { "decision", "findings", "summary" },
                additionalProperties = false
            },
            _ => new
            {
                type = "object",
                properties = new
                {
                    outcome = StringEnum("CANDIDATE_PREPARED", "STOP", "ESCALATE"),
                    summary = new { type = "string", maxLength = 800 }
                },
                required = new[] { "outcome", "summary" },
                additionalProperties = false
            }
        };
    }

    private static EngineeringWorkItemSnapshot ClearSession(EngineeringWorkItemSnapshot item)
        => item with
        {
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null,
            AgentSessionId = null,
            AgentSessionRole = null,
            AgentSessionUpdatedUtc = DateTime.UtcNow
        };

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string apiKey, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "agents=v1");
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private static object Failure(string code, int? status = null)
        => new { ok = false, error = code, providerStatus = status };

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadInt64(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.TryGetInt64(out var result) ? result : null;

    private static bool IsSafeSessionId(string value)
        => value.Length is > 0 and <= 160 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
