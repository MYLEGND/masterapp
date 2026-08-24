from pathlib import Path

service_path = Path('AgentPortal/Services/LegendFounderAiConversationService.cs')
tests_path = Path('AgentPortal.Tests/LegendFounderAiConversationRoutingTests.cs')
js_path = Path('AgentPortal/wwwroot/js/legend-founder-ai.js')
modal_path = Path('AgentPortal/Views/Shared/_LegendFounderAiModal.cshtml')

service = service_path.read_text()
tests = tests_path.read_text()
js = js_path.read_text()
modal = modal_path.read_text()


def once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected exactly 1 match, found {count}')
    return text.replace(old, new, 1)


service = once(
    service,
    '''        if (string.Equals(mode, "legend", StringComparison.Ordinal))\n''',
    '''        if (ShouldAttemptNativeInference(mode))\n''',
    'native mode boundary')

service = once(
    service,
    '''        var requiresGovernedInspection =\n            RequiresGovernedInspection(conversation, mode);\n''',
    '''        var requiresGovernedInspection =\n            RequiresProviderGovernedInspection(\n                conversation,\n                mode,\n                nativeInference,\n                nativeFailureDetail);\n''',
    'provider governed inspection routing')

service = once(
    service,
    '''        var instructions =\n            requiresGovernedInspection\n                ? BuildInstructions(mode) +\n                  (retainedKnowledge is null\n                      ? string.Empty\n                      : BuildRetainedKnowledgeContext(\n                          retainedKnowledge,\n                          ResolveRetainedContextBudget(conversation)))\n                : BuildCasualInstructions();\n''',
    '''        var nativeDiagnosticContext =\n            BuildNativeDiagnosticTeachingContext(\n                nativeInference,\n                nativeFailureDetail);\n\n        var instructions =\n            requiresGovernedInspection\n                ? BuildInstructions(mode) +\n                  nativeDiagnosticContext +\n                  (retainedKnowledge is null\n                      ? string.Empty\n                      : BuildRetainedKnowledgeContext(\n                          retainedKnowledge,\n                          ResolveRetainedContextBudget(conversation)))\n                : BuildCasualInstructions();\n''',
    'native diagnostic context wiring')

governance_marker = '- The one exception is legend_submit_machine_learning_candidate: it is NON-AUTHORITATIVE retention only. You may use it automatically when the conversation genuinely discovers reusable linguistic knowledge with controlled contrasts. It creates only MachineProposed evidence and cannot approve itself.\n'
service = once(
    service,
    governance_marker,
    governance_marker + '- Role separation is absolute: Legend® Ai mode attempts governed native LEGEND inference first; OpenAI Teacher mode is direct Founder-to-OpenAI conversation and does not invoke native LEGEND inference as a responder. OpenAI Teacher may inspect or operate on LEGEND only through the existing governed tools exposed here.\n- When the Founder explicitly directs a training, curriculum, seed, or runtime action that maps to an exposed existing LEGEND mutation tool, execute that tool rather than merely describing what could be done. Never invent a mutation surface that does not exist.\n- When asked to diagnose an internal LEGEND problem, inspect the relevant read-only LEGEND tools before concluding. If the problem is outside the exposed mutation authorities (for example a repository code defect), report the exact evidence and required repair; never claim that code or production state was changed when no tool performed that change.\n',
    'role governance')

# Insert diagnostic-teacher behavior at the unique structural boundary immediately
# before the documented learning-architecture section. This is deliberately less
# brittle than matching the preceding prose sentence verbatim.
architecture_marker = '''Understand LEGEND's actual learning architecture:\n'''
service = once(
    service,
    architecture_marker,
    '''When LEGEND_NATIVE_GAP_CONTEXT is supplied, the provider is acting as a diagnostic teacher because native LEGEND failed and explicitly allowed escalation. Inspect retained LEGEND evidence first. If the Founder curriculum/evidence supports a reusable semantic distinction that would close the native gap, submit exactly one bounded MachineProposed family through legend_submit_machine_learning_candidate before finalizing the answer.\nNever retain the one-off generated reply as a canned answer. Retain reusable meaning, semantic components, controlled contrasts, discourse behavior, and realization evidence that explain how the class of utterance should be understood and composed.\nIf retained evidence is insufficient or contradictory, do not fabricate curriculum. State the exact missing evidence/contrast so the Founder and existing autonomous learning authorities can resolve it.\n\nUnderstand LEGEND's actual learning architecture:\n''',
    'native gap retention governance')

service = once(
    service,
    '''MODE: OPENAI TEACHER\n\nYou are the external OpenAI Teacher speaking directly with the Founder.\n''',
    '''MODE: OPENAI TEACHER\n\nYou are the external OpenAI Teacher speaking directly with the Founder.\nNative LEGEND conversational inference is bypassed in this mode. You are not a second LEGEND responder and must never speak as though a native LEGEND answer was produced.\n''',
    'teacher direct role')

helper_marker = '''    private static string BuildCasualInstructions() =>\n'''
helper = '''    private static string BuildNativeDiagnosticTeachingContext(\n        LegendConnectNativeInferenceSnapshot? nativeInference,\n        string? nativeFailureDetail)\n    {\n        if (nativeInference is not { Supported: false, RequiresEscalation: true } &&\n            string.IsNullOrWhiteSpace(nativeFailureDetail))\n        {\n            return string.Empty;\n        }\n\n        var reasonCode = string.IsNullOrWhiteSpace(nativeInference?.ReasonCode)\n            ? "native_inference_unavailable"\n            : nativeInference.ReasonCode.Trim();\n        var authorityDetail = !string.IsNullOrWhiteSpace(nativeInference?.AuthoritySummary)\n            ? NormalizeFailureDetail(nativeInference.AuthoritySummary)\n            : "The native authority returned no additional governed summary.";\n        var failureDetail = string.IsNullOrWhiteSpace(nativeFailureDetail)\n            ? "No native execution exception was recorded."\n            : NormalizeFailureDetail(nativeFailureDetail);\n        var evidenceCount = nativeInference?.EvidenceCount ?? 0;\n\n        return $"""\n\nLEGEND_NATIVE_GAP_CONTEXT:\nNativeReasonCode={reasonCode}\nNativeAuthorityDetail={authorityDetail}\nNativeEvidenceCount={evidenceCount}\nNativeExecutionDetail={failureDetail}\n\nDIAGNOSTIC TEACHER REQUIREMENTS:\n- This turn reached OpenAI because native LEGEND could not produce one governed answer and explicitly permitted escalation.\n- Diagnose the missing linguistic/semantic capability against retained LEGEND evidence before relying on general OpenAI recall.\n- Use legend_search_retained_knowledge when a narrower query can distinguish an unknown component, ambiguous composition, missing transition, contradiction, realization gap, discourse gap, or production-eligibility gap.\n- If governed evidence supports a reusable controlled semantic family that would reduce recurrence, submit exactly one bounded MachineProposed family through legend_submit_machine_learning_candidate before the final response.\n- Preserve reusable semantics and controlled contrasts, not a generated response template.\n- If a valid proposal cannot be supported, state precisely what governed evidence is missing instead of inventing it.\n- MachineProposed retention is not canonical approval. The existing independent critic, validator, curriculum admission, evaluator, training, and promotion authorities remain mandatory.\n""";\n    }\n\n'''
service = once(service, helper_marker, helper + helper_marker, 'diagnostic teaching context helper')

routing_marker = '''    private static string ResolveReasoningEffortForRound(\n'''
routing_helpers = '''    private static bool ShouldAttemptNativeInference(string mode) =>\n        string.Equals(mode, "legend", StringComparison.Ordinal);\n\n    private static bool RequiresProviderGovernedInspection(\n        IReadOnlyList<LegendFounderAiChatMessage> conversation,\n        string mode,\n        LegendConnectNativeInferenceSnapshot? nativeInference,\n        string? nativeFailureDetail) =>\n        RequiresGovernedInspection(conversation, mode) ||\n        nativeInference is { Supported: false, RequiresEscalation: true } ||\n        !string.IsNullOrWhiteSpace(nativeFailureDetail);\n\n'''
service = once(service, routing_marker, routing_helpers + routing_marker, 'mode and provider routing helpers')

set_mode_old = '''    function setMode(nextMode) {\n        if (busy) {\n            return;\n        }\n\n        const conversation = activeConversation();\n\n        conversation.mode =\n            nextMode === 'teacher'\n                ? 'teacher'\n                : 'legend';\n\n        conversation.updatedUtc =\n            new Date().toISOString();\n\n        saveState();\n        setReadingMode(false);\n        renderAll({ forceBottom: false });\n        focusComposer();\n    }\n'''
set_mode_new = '''    function setMode(nextMode) {\n        if (busy) {\n            return;\n        }\n\n        const requestedMode =\n            nextMode === 'teacher'\n                ? 'teacher'\n                : 'legend';\n\n        const current = activeConversation();\n\n        if (current.mode === requestedMode) {\n            return;\n        }\n\n        // One browser conversation has exactly one responder identity. Never\n        // relabel an existing Legend® Ai transcript as OpenAI Teacher (or the\n        // reverse), because that would feed one AI's prior responses to the\n        // other under the wrong role. A mode change starts a clean thread while\n        // preserving both histories independently.\n        const conversation = newConversationRecord(requestedMode);\n        state.conversations.unshift(conversation);\n        state.activeConversationId = conversation.id;\n        saveState();\n        setSidebarOpen(false);\n        setReadingMode(false);\n        renderAll({ forceBottom: true });\n\n        if (status) {\n            status.textContent = '';\n        }\n\n        focusComposer();\n    }\n'''
js = once(js, set_mode_old, set_mode_new, 'immutable UI role switch')

js = once(
    js,
    '''                conversation.mode === 'teacher'\n                    ? 'External language teacher & strategy'\n                    : 'Governed intelligence conversation';\n''',
    '''                conversation.mode === 'teacher'\n                    ? 'Direct OpenAI Teacher · LEGEND native inference bypassed'\n                    : 'Legend® Ai · governed native intelligence first';\n''',
    'mode subtitle clarity')

modal = once(
    modal,
    '''                            OpenAI Teacher\n                        </button>\n''',
    '''                            OpenAI Teacher\n                            <span class="visually-hidden">Direct OpenAI mode; native LEGEND responder bypassed</span>\n                        </button>\n''',
    'teacher button accessibility label')

modal = once(
    modal,
    '''                            <p>\n                                Inspect governed knowledge, language gaps,\n                                evidence, training generations, model state,\n                                readiness, provider dependence, and the next\n                                legitimate V14 learning step.\n                            </p>\n''',
    '''                            <p>\n                                Legend® Ai mode uses governed native intelligence first.\n                                OpenAI Teacher mode is a direct Founder-to-OpenAI channel\n                                that can inspect and teach through the existing governed\n                                LEGEND tools without becoming a second authority.\n                            </p>\n''',
    'welcome role explanation')

test_marker = '''    [Fact]\n    public void NativeFailureResponse_ExposesGovernedReasonAndProviderFailureDetail()\n'''
new_tests = '''    [Theory]\n    [InlineData("legend", true)]\n    [InlineData("teacher", false)]\n    public void ConversationMode_ExplicitlyControlsNativeInference(string mode, bool expected)\n    {\n        var method = typeof(LegendFounderAiConversationService)\n            .GetMethod("ShouldAttemptNativeInference", BindingFlags.NonPublic | BindingFlags.Static);\n        Assert.NotNull(method);\n        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(null, new object[] { mode })));\n    }\n\n    [Fact]\n    public void OpenAiTeacherInstructions_DeclareDirectRoleAndNativeBypass()\n    {\n        var method = typeof(LegendFounderAiConversationService)\n            .GetMethod("BuildInstructions", BindingFlags.NonPublic | BindingFlags.Static);\n        Assert.NotNull(method);\n        var instructions = Assert.IsType<string>(method!.Invoke(null, new object[] { "teacher" }));\n        Assert.Contains("external OpenAI Teacher speaking directly with the Founder", instructions);\n        Assert.Contains("Native LEGEND conversational inference is bypassed in this mode", instructions);\n        Assert.Contains("existing governed tools", instructions);\n        Assert.Contains("execute that tool rather than merely describing", instructions);\n    }\n\n    [Fact]\n    public void CasualNativeEscalation_EntersGovernedDiagnosticTeacherPath()\n    {\n        var method = typeof(LegendFounderAiConversationService)\n            .GetMethod("RequiresProviderGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);\n        Assert.NotNull(method);\n        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "Hi")];\n        var snapshot = new LegendConnectNativeInferenceSnapshot(\n            false, 0m, null, "ambiguous_composed_meaning", 0,\n            "No unique governed semantic transition could be selected.", true);\n        Assert.True(Assert.IsType<bool>(method!.Invoke(null, new object?[] { conversation, "legend", snapshot, null })));\n    }\n\n    [Fact]\n    public void CasualNativeSuccess_DoesNotEnterProviderInspectionPath()\n    {\n        var method = typeof(LegendFounderAiConversationService)\n            .GetMethod("RequiresProviderGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);\n        Assert.NotNull(method);\n        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "How are you?")];\n        var snapshot = new LegendConnectNativeInferenceSnapshot(\n            true, 1m, "I'm doing great, thanks.", "supported", 4,\n            "Governed native response selected.", false);\n        Assert.False(Assert.IsType<bool>(method!.Invoke(null, new object?[] { conversation, "legend", snapshot, null })));\n    }\n\n    [Fact]\n    public void NativeGapContext_RequiresEvidenceFirstRetentionWithoutSelfPromotion()\n    {\n        var method = typeof(LegendFounderAiConversationService)\n            .GetMethod("BuildNativeDiagnosticTeachingContext", BindingFlags.NonPublic | BindingFlags.Static);\n        Assert.NotNull(method);\n        var snapshot = new LegendConnectNativeInferenceSnapshot(\n            false, 0m, null, "meaning_graph_component_unknown", 0,\n            "A required meaning component was unknown.", true);\n        var context = Assert.IsType<string>(method!.Invoke(null, new object?[] { snapshot, null }));\n        Assert.Contains("LEGEND_NATIVE_GAP_CONTEXT", context);\n        Assert.Contains("meaning_graph_component_unknown", context);\n        Assert.Contains("legend_search_retained_knowledge", context);\n        Assert.Contains("legend_submit_machine_learning_candidate", context);\n        Assert.Contains("MachineProposed", context);\n        Assert.Contains("independent critic", context);\n        Assert.Contains("instead of inventing", context, StringComparison.OrdinalIgnoreCase);\n    }\n\n'''
tests = once(tests, test_marker, new_tests + test_marker, 'role and learning regression tests')

service_path.write_text(service)
tests_path.write_text(tests)
js_path.write_text(js)
modal_path.write_text(modal)
