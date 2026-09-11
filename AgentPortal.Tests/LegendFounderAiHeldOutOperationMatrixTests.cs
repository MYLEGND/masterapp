using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Held-out operation matrix. Admitted-evidence cases prove that their request
/// and generated answer are absent from the governed corpus; the broader
/// capability gates also retain their original novel wording. The matrix exercises the real
/// native inference authority (no mocked <see cref="ILegendConnectOperations"/>)
/// and records the authority, stage, reason, evidence count, model provenance,
/// provider HTTP calls, tool calls and database writes for each exchange.
/// The recorded matrix is written to LEGEND_HELDOUT_MATRIX_PATH when that
/// variable is set, so the exact prompts and actual responses can be preserved.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiHeldOutOperationMatrixTests
{
    /// <summary>
    /// A variable-subject deduction uses the existing Founder curriculum and
    /// original realization authority end to end. Wren is admitted only as a
    /// bird; no Wren mortality conclusion, request/answer pair, or expected
    /// reply is admitted. The Robin/Lark/Swift facts supply the reusable rule.
    /// </summary>
    [Theory]
    [InlineData("Wren bird.")]
    [InlineData("Bird Wren.")]
    public async Task AdmittedDeduction_RecombinesAHeldOutSubjectAndProducesAnOriginalNativeAnswer(
        string prompt)
    {
        using var environment = new FounderEnvironmentScope();
        using var writes = new WriteAttemptSentinel();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
            async (services, db, externalCounts) =>
            {
                var founder = await AddFounderProfileAsync(db);
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                await AdmitDeductionCorpusAsync(curriculum);
                var corpusTexts = await db.LegendLanguageTextUnits
                    .Select(item => item.Text).ToArrayAsync();
                Assert.DoesNotContain(corpusTexts, text => SameText(text, prompt));
                Assert.DoesNotContain(corpusTexts, text =>
                    text.Contains("Wren", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("mortal", StringComparison.OrdinalIgnoreCase));

                var operations = services.GetRequiredService<ILegendConnectOperations>();
                writes.Arm();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(prompt);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                Assert.Contains(graph.Nodes, node =>
                    node.SemanticDimension == "subject" && node.SemanticValue == "wren");
                Assert.Contains(graph.Nodes, node =>
                    node.SemanticDimension == "kind" && node.SemanticValue == "bird");
                var planned = await operations.TryPlanConversationAsync(
                    prompt, new LegendConnectDiscourseStateSnapshot([]));
                Assert.True(planned.Supported, planned.ReasonCode);
                var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
                Assert.Equal("wren", plan.ResultDimensions["subject"]);
                Assert.Equal("mortal", plan.ResultDimensions["mortality"]);
                Assert.NotEmpty(plan.ReasoningTransitionPath ?? []);
                Assert.True(plan.ReasoningEvidenceCount >= 3);

                var native = await operations.TryInferConversationWithDiscourseAsync(
                    prompt, [], new LegendConnectDiscourseStateSnapshot([]),
                    CancellationToken.None, "en", LegendConnectExternalProviderPolicy.NativeOnly);
                Assert.True(native.Supported, native.ReasonCode + "; " + native.AuthoritySummary);
                Assert.False(native.RequiresEscalation);
                Assert.True(native.EvidenceCount >= 3);
                Assert.NotNull(native.Answer);
                Assert.Contains("Wren", native.Answer!, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("mortal", native.Answer!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(corpusTexts, text => SameText(text, native.Answer!));

                var response = await services.GetRequiredService<LegendFounderAiConversationService>()
                    .ReplyAsync(founder, new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        NativeOnly = true,
                        SourceLanguageCode = "en",
                        Messages = [new("user", prompt)]
                    });
                Assert.True(response.Succeeded, response.Error);
                Assert.Equal("LegendAi", response.ResponseAuthority);
                Assert.Equal("native_response", response.Stage);
                Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, response.EvidenceOrigin);
                Assert.Equal(native.Answer, response.Message);
                Assert.Equal((0, 0), externalCounts());
                Assert.Equal(0, writes.OperationalWriteAttempts);
                Assert.Empty(writes.ObservedWriteEntities);
                Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
            }, writes);
    }

    [Fact]
    public async Task MissingDeductionRule_CannotProjectAnUnobservedFactFromASharedSubject()
    {
        using var environment = new FounderEnvironmentScope();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
            async (services, db, externalCounts) =>
            {
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                await AdmitDeductionCorpusAsync(curriculum, includeReasoningRule: false);
                var operations = services.GetRequiredService<ILegendConnectOperations>();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync("Wren bird.");
                Assert.True(graph.IsComposed, graph.ReasonCode);
                Assert.Contains(graph.Nodes, node => node.SemanticDimension == "subject" && node.SemanticValue == "wren");
                Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == "mortality");
                var plan = await operations.TryPlanConversationAsync("Wren bird.", new LegendConnectDiscourseStateSnapshot([]));
                Assert.False(plan.Supported,
                    "The response source requires mortality, which neither the source graph nor an admitted rule supplies.");
                Assert.Null(plan.Plan);
                var native = await operations.TryInferConversationWithDiscourseAsync(
                    "Wren bird.", [], new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None,
                    "en", LegendConnectExternalProviderPolicy.NativeOnly);
                Assert.False(native.Supported);
                Assert.Null(native.Answer);
                Assert.Equal((0, 0), externalCounts());
            });
    }

    [Theory]
    [InlineData("verification_state", "verified", "Wren bird.")]
    [InlineData("inventory_state", "available", "Wren bird.")]
    [InlineData("verification_state", "verified", "Robin")]
    [InlineData("inventory_state", "available", "Robin")]
    public async Task UnobservedSemanticPremise_CannotBeDroppedBecauseItHasNoLiteralMeaningNode(
        string premiseDimension, string premiseValue, string currentRequest)
    {
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
            async (services, db, externalCounts) =>
            {
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                foreach (var subject in new[] { "Robin", "Lark", "Swift" })
                {
                    var value = subject.ToLowerInvariant();
                    var submitted = await curriculum.SubmitFounderBatchAsync(
                        new LegendConnectCurriculumBatchSubmission(
                            "reasoning.required-state." + value,
                            "A factual semantic precondition without a literal surface anchor",
                            [
                                new("The authorized case concerns " + subject + ".",
                                    new Dictionary<string, string> { ["subject"] = value, [premiseDimension] = premiseValue },
                                    new LegendConnectMeaningGraphSubmission([new("subject", "subject", value, subject)], [])),
                                new(subject + " is cleared.",
                                    new Dictionary<string, string> { ["subject"] = value, ["decision"] = "cleared" },
                                    new LegendConnectMeaningGraphSubmission(
                                        [new("subject", "subject", value, subject), new("decision", "decision", "cleared", "cleared")], [])),
                                DeductionExample("The " + subject + " observation also identifies Wren as a bird.",
                                    "Wren", "kind", "bird", "member-of")
                            ],
                            [new LegendConnectSemanticTransitionSubmission(
                                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                                {
                                    ["subject"] = "$subject", [premiseDimension] = premiseValue
                                }),
                                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                                {
                                    ["subject"] = "$subject", ["decision"] = "cleared"
                                }))]));
                    Assert.True(submitted.Succeeded, submitted.Message);
                }
                var operations = services.GetRequiredService<ILegendConnectOperations>();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(currentRequest);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                Assert.DoesNotContain(graph.Nodes, node => node.SemanticDimension == premiseDimension);
                if (currentRequest == "Robin")
                {
                    // This is exactly the admitted source's one-node graph,
                    // with the same subject. Graph equality still cannot grant
                    // its omitted, nonliteral factual precondition.
                    var node = Assert.Single(graph.Nodes);
                    Assert.Equal("subject", node.SemanticDimension);
                    Assert.Equal("robin", node.SemanticValue);
                }
                var plan = await operations.TryPlanConversationAsync(currentRequest, new LegendConnectDiscourseStateSnapshot([]));
                Assert.False(plan.Supported,
                    "A declared source-frame fact remains required even when it has no literal meaning node: " + premiseDimension);
                Assert.Null(plan.Plan);
                Assert.Equal((0, 0), externalCounts());
            });
    }

    /// <summary>
    /// A conflict between two admitted response transitions is governed
    /// ambiguity even when the result record reports zero evidence. The
    /// provider-enabled control must preserve it, while genuinely unknown
    /// source meaning remains eligible for the configured teacher boundary.
    /// </summary>
    [Fact]
    public async Task AdmittedConflictingTransitions_DoNotEscalateWhileUnknownSourceRemainsEligible()
    {
        using var environment = new FounderEnvironmentScope();
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
            async (services, db, externalCounts) =>
            {
                var founder = await AddFounderProfileAsync(db);
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                foreach (var perspective in new[] { "inspection", "review", "audit" })
                {
                    var submitted = await curriculum.SubmitFounderBatchAsync(
                        ConflictingGateCorpus(perspective));
                    Assert.True(submitted.Succeeded, submitted.Message);
                }
                var operations = services.GetRequiredService<ILegendConnectOperations>();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync("Observed.");
                Assert.True(graph.IsComposed, graph.ReasonCode);
                var conflict = await operations.TryInferConversationWithDiscourseAsync(
                    "Observed.", [], new LegendConnectDiscourseStateSnapshot([]),
                    CancellationToken.None, "en", LegendConnectExternalProviderPolicy.ProviderEnabled);
                Assert.False(conflict.Supported);
                Assert.Equal("ambiguous_semantic_transition", conflict.ReasonCode);
                Assert.False(conflict.RequiresEscalation);
                Assert.Null(conflict.Answer);
                var response = await services.GetRequiredService<LegendFounderAiConversationService>().ReplyAsync(
                    founder, new LegendFounderAiChatRequest
                    {
                        Mode = "legend", NativeOnly = false, SourceLanguageCode = "en",
                        Messages = [new("user", "Observed.")]
                    });
                Assert.Equal("SystemDiagnostic", response.ResponseAuthority);

                var unknown = await operations.TryInferConversationWithDiscourseAsync(
                    "Describe the ultraviolet topography of an uncharted moon.", [],
                    new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None,
                    "en", LegendConnectExternalProviderPolicy.ProviderEnabled);
                Assert.False(unknown.Supported);
                Assert.True(unknown.RequiresEscalation, unknown.ReasonCode);
                Assert.Equal((0, 0), externalCounts());
            });
    }

    /// <summary>
    /// A well-supported static curriculum answer cannot authorize a current
    /// owned-record count. The graph and response plan are both real, so this
    /// fails only if native serving mistakes stored articulation for the exact
    /// authenticated read receipt required by the request's admitted relation.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdmittedOwnedRecordRequest_CannotServeAStaticCountWithoutItsExactReadReceipt(
        bool nativeOnly)
    {
        using var environment = new FounderEnvironmentScope();
        using var writes = new WriteAttemptSentinel();
        const string prompt = "Inspect the client portfolio.";
        const string staleAnswer = "The client portfolio contains 999 active clients.";
        await LegendFounderAiNativeOnlyProviderIsolationTests.WithProductionAuthorityAsync(
            async (services, db, externalCounts) =>
            {
                var founder = await AddFounderProfileAsync(db);
                ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
                var curriculum = services.GetRequiredService<LegendConnectCurriculumService>();
                var submitted = await curriculum.SubmitFounderBatchAsync(
                    new LegendConnectCurriculumBatchSubmission(
                        "governed.portfolio.stale-count",
                        "An owned-record inspection with an obsolete static answer",
                        [
                            new(prompt, new Dictionary<string, string>
                            {
                                ["conversation_function"] = "portfolio_inspection",
                                ["record_scope"] = "client_portfolio"
                            }, new LegendConnectMeaningGraphSubmission(
                                [new("action", "conversation_function", "portfolio_inspection", "Inspect"),
                                 new("scope", "record_scope", "client_portfolio", "client portfolio")],
                                [new("action", LegendConnectOwnedRecordRequest.RequiredRelationKind, "scope")])),
                            new(staleAnswer, new Dictionary<string, string>
                            {
                                ["conversation_function"] = "portfolio_count_answer"
                            })
                        ],
                        [new LegendConnectSemanticTransitionSubmission(
                            new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                            {
                                ["conversation_function"] = "portfolio_inspection"
                            }),
                            new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                            {
                                ["conversation_function"] = "portfolio_count_answer"
                            }))]));
                Assert.True(submitted.Succeeded, submitted.Message);
                var operations = services.GetRequiredService<ILegendConnectOperations>();
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(prompt);
                Assert.True(graph.IsComposed, graph.ReasonCode);
                Assert.True(LegendConnectOwnedRecordRequest.Classify(graph).RequiresGovernedReadReceipt);
                var plan = await operations.TryPlanConversationAsync(prompt, new LegendConnectDiscourseStateSnapshot([]));
                Assert.True(plan.Supported, plan.ReasonCode);
                Assert.Equal("portfolio_count_answer", plan.Plan!.ResultDimensions["conversation_function"]);
                writes.Arm();

                var inference = await operations.TryInferConversationWithDiscourseAsync(
                    prompt, [], new LegendConnectDiscourseStateSnapshot([]), CancellationToken.None,
                    "en", nativeOnly ? LegendConnectExternalProviderPolicy.NativeOnly : LegendConnectExternalProviderPolicy.ProviderEnabled);
                Assert.False(inference.Supported,
                    "A current portfolio count cannot be served from the static curriculum endpoint: " + inference.Answer);
                Assert.Null(inference.Answer);
                Assert.Equal("owned_record_read_scope_unproven", inference.ReasonCode);
                Assert.False(inference.RequiresEscalation);
                Assert.True(inference.OwnedRecordIntent?.RequiresGovernedReadReceipt);
                Assert.Null(inference.ReadOnlyContentRequest);

                var response = await services.GetRequiredService<LegendFounderAiConversationService>().ReplyAsync(
                    founder, new LegendFounderAiChatRequest
                    {
                        Mode = "legend", NativeOnly = nativeOnly, SourceLanguageCode = "en",
                        Messages = [new("user", prompt)]
                    });
                Assert.Equal("SystemDiagnostic", response.ResponseAuthority);
                Assert.DoesNotContain("999", response.Message ?? string.Empty, StringComparison.Ordinal);
                Assert.Equal((0, 0), externalCounts());
                Assert.Equal(0, writes.OperationalWriteAttempts);
                Assert.Empty(writes.ObservedWriteEntities);
            }, writes);
    }

    private static bool SameText(string first, string second) =>
        string.Equals(LegendLanguageIdentity.NormalizeText(first),
            LegendLanguageIdentity.NormalizeText(second), StringComparison.Ordinal);

    // These examples adapt DeductionSurfaceFamily in the existing governed
    // executor suite. Distinct subjects and utterances replace numbered copies;
    // all facts and transitions use canonical Founder admission.
    private static async Task AdmitDeductionCorpusAsync(
        LegendConnectCurriculumService curriculum, bool includeReasoningRule = true)
    {
        var examples = new[]
        {
            (Subject: "Robin", Premise: "Robin is a bird.", Conclusion: "Robin is mortal.", Wren: "The bird named Wren is present."),
            (Subject: "Lark", Premise: "The bird in this observation is Lark.", Conclusion: "The mortal subject is Lark.", Wren: "This observation identifies Wren as a bird."),
            (Subject: "Swift", Premise: "This classification records Swift as a bird.", Conclusion: "Swift has the mortal property.", Wren: "Wren belongs to the bird class.")
        };
        foreach (var example in examples)
        {
            var subject = example.Subject.ToLowerInvariant();
            var submitted = await curriculum.SubmitFounderBatchAsync(
                new LegendConnectCurriculumBatchSubmission(
                    "reasoning.deduction.heldout." + subject,
                    "Bird classification and mortality with a reusable subject",
                    [
                        DeductionExample(example.Premise, example.Subject, "kind", "bird", "member-of", "premise-" + subject),
                        DeductionExample(example.Conclusion, example.Subject, "mortality", "mortal", "has-property", "conclusion-" + subject),
                        DeductionExample(example.Wren, "Wren", "kind", "bird", "member-of"),
                        DeductionExample(example.Subject + " is mortal; this follows from its classification.",
                            example.Subject, "mortality", "mortal", "has-property", response: true)
                    ],
                    [new LegendConnectSemanticTransitionSubmission(
                        new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                        {
                            ["subject"] = "$subject", ["mortality"] = "mortal"
                        }),
                        new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                        {
                            ["subject"] = "$subject", ["mortality"] = "mortal",
                            ["conversation_function"] = "deductive_answer"
                        }))]));
            Assert.True(submitted.Succeeded, submitted.Message);
            if (includeReasoningRule)
            {
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new LegendConnectCrossExampleSemanticRelationshipSubmission(
                        "premise-" + subject, "reasoning.deduction.universal.mortality", "conclusion-" + subject),
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }
        }
    }

    private static LegendConnectCurriculumExampleSubmission DeductionExample(
        string text, string subject, string dimension, string value, string relation,
        string? exampleKey = null, bool response = false)
    {
        var dimensions = new Dictionary<string, string>
        {
            ["subject"] = subject.ToLowerInvariant(), [dimension] = value
        };
        if (response)
            dimensions["conversation_function"] = "deductive_answer";
        return new LegendConnectCurriculumExampleSubmission(text, dimensions,
            new LegendConnectMeaningGraphSubmission(
                [new("subject", "subject", subject.ToLowerInvariant(), subject),
                 new("property", dimension, value, value)],
                [new("subject", relation, "property")]), exampleKey);
    }

    private static LegendConnectCurriculumBatchSubmission ConflictingGateCorpus(string perspective) =>
        new("reasoning.conflict." + perspective,
            "Incompatible governed conclusions from the same observation",
            [
                new("The gate is observed during " + perspective + ".",
                    new Dictionary<string, string> { ["gate_state"] = "observed" },
                    new LegendConnectMeaningGraphSubmission(
                        [new("state", "gate_state", "observed", "observed")], [])),
                new("The " + perspective + " reports that the gate is open.",
                    new Dictionary<string, string> { ["gate_conclusion"] = "open" }),
                new("The " + perspective + " reports that the gate is closed.",
                    new Dictionary<string, string> { ["gate_conclusion"] = "closed" })
            ],
            new[] { "open", "closed" }.Select(conclusion =>
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string> { ["gate_state"] = "observed" }),
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string> { ["gate_conclusion"] = conclusion }))).ToArray());

    private const string ArithmeticPrompt =
        "A dispatch board opens with 41 open tickets. Twelve are closed, five new tickets arrive, and four of the remaining tickets are flagged for review. How many tickets are still open, and what share of them are flagged? Show the calculation.";

    private const string RewritingPrompt =
        "Rewrite this rough note as a concise professional update for a regional supervisor without changing any facts: we checked nine renewals, two lack proof of payment, Devon will call those households before Monday, nothing has been reinstated yet.";

    private const string DeductionPrompt =
        "Every audited file carries a signed attestation. File Kestrel was audited and carries no signed attestation. What follows logically, and which premise must be false if both records describe the same file?";

    private const string CausalPrompt =
        "Intake forms began arriving with blank effective dates the same week two intake clerks were reassigned and a form template was republished. Which explanation best accounts for the pattern, and what observation would rule it out?";

    private const string PlanningPrompt =
        "Thirty-one overdue inspections must be cleared at seven per day, hazardous sites first, and no site may be contacted twice. Give the daily sequence and the completion day.";

    private const string UncertaintyPrompt =
        "What was our exact client renewal percentage for the third quarter of last year in the portal? Do not estimate.";

    private const string MemoryFirstPrompt =
        "Record two facts for this conversation: Project Marlin closes on the ninth of November, and Corine owns the vendor review.";

    private const string MemoryFollowUpPrompt =
        "Using only what I told you in this conversation, who owns the vendor review and what is the closing date?";

    private const string CreolePrompt =
        "Yon dosye gen ven-senk fòm. Rapò a di gen ven-sèt fòm ki resevwa. Ki kontradiksyon ki genyen, e ki chif ki dwe verifye anvan nou kontinye?";

    private const string ToolPrompt =
        "Inspect, read-only, how many client records and how many workstation leads are currently visible to me.";

    /// <summary>
    /// Isolation evidence only. It proves the native-only boundary makes no
    /// provider call and attempts no write, and that an unanswered turn always
    /// carries an explicit governed reason. It deliberately does NOT judge
    /// capability - and it does not require a refusal either, so a correct
    /// native answer keeps it green. Capability is judged by the gates below.
    /// </summary>
    [Fact]
    public async Task NativeOnlyRequests_MakeNoProviderCallAndAttemptNoWrite()
    {
        var rows = new List<MatrixRow>();

        foreach (var (category, prompt) in CapabilityMatrix)
        {
            rows.Add(await RunAsync(
                $"native_only:{category}",
                prompt,
                nativeOnly: true,
                sourceLanguageCode: category == "haitian_creole_conflict" ? "ht" : "en"));
        }

        Record(rows);

        Assert.All(rows, row => AssertNativeBoundary(row));

        // A row is never required to refuse. It must either be answered under
        // LEGEND authority or declare an explicit governed reason.
        Assert.All(rows, row =>
        {
            if (!IsAnswered(row))
            {
                AssertGovernedRefusal(row);
            }
        });
    }

    /// <summary>
    /// The native capability gate. Every requested category is covered and
    /// every row is success-required: a governed refusal is a FAILURE here, by
    /// Founder instruction. A passing row must be answered under LEGEND
    /// authority, carry every required semantic element of the correct answer,
    /// carry non-provider provenance, and prove the absolute native boundary.
    /// </summary>
    [Theory]
    [MemberData(nameof(NativeCapabilityCases))]
    public async Task NativeOnlyCapability_ProducesTheCorrectAnswerUnderLegendAuthority(
        string category,
        string[] requiredAnswerElements)
    {
        var prompt = CapabilityMatrix.Single(row => row.Category == category).Prompt;

        var row = await RunAsync(
            $"native_capability:{category}",
            prompt,
            nativeOnly: true,
            sourceLanguageCode: category == "haitian_creole_conflict" ? "ht" : "en");

        Record([row]);

        AssertNativeCapability(row, requiredAnswerElements);
        AssertCapabilitySemantics(category, row.Message!);
    }

    private static void AssertCapabilitySemantics(string category, string answer)
    {
        var normalized = answer.ToLowerInvariant();
        switch (category)
        {
            case "rewriting":
                Assert.Contains("proof of payment", normalized);
                Assert.Matches(@"(?:not|none|no)[^.]*reinstat|reinstat[^.]*(?:not|none|no)", normalized);
                break;
            case "deduction":
                Assert.Matches(@"cannot both|can't both|inconsisten|contradict", normalized);
                // The records establish inconsistency, not which individual
                // premise is false. Choosing one without further evidence fails.
                Assert.Matches(@"cannot (?:determine|identify)|can't (?:determine|identify)|at least one|one or more|not enough|insufficient", normalized);
                break;
            case "causal_diagnosis":
                // Both changes coincide with the outcome. Correct diagnosis
                // preserves competing causes and proposes a discriminating check.
                Assert.Matches(@"cannot|can't|insufficient|not enough|either|both", normalized);
                Assert.Matches(@"compar|check|test|unchanged|old template|previous template", normalized);
                break;
            case "constrained_planning":
                Assert.Contains("7", normalized);
                Assert.Contains("3", normalized);
                Assert.Matches(@"once|duplicate|repeat|unique", normalized);
                break;
            case "internal_data_uncertainty":
                Assert.Matches(@"cannot|can't|unavailable|missing|insufficient|not available", normalized);
                Assert.DoesNotMatch(@"\b\d+(?:\.\d+)?\s*%", normalized);
                break;
        }
    }

    public static TheoryData<string, string[]> NativeCapabilityCases()
    {
        var cases = new TheoryData<string, string[]>();
        // 41 - 12 + 5 = 34 tickets still open; 4 of 34 flagged = 2/17.
        cases.Add("arithmetic", ["34", "2/17"]);
        // A faithful rewrite must preserve every stated fact and add none.
        cases.Add("rewriting", ["nine", "two", "Devon", "Monday"]);
        // The premise set is inconsistent; no individual premise is uniquely disproved.
        cases.Add("deduction", ["Kestrel", "premise"]);
        // Coincident changes do not distinguish clerk reassignment from a template defect.
        cases.Add("causal_diagnosis", ["template", "clerk"]);
        // 31 inspections at 7 per day completes on day five.
        cases.Add("constrained_planning", ["five", "hazardous"]);
        // Internal data must be answered from an authenticated governed read.
        cases.Add("internal_data_uncertainty", ["governed"]);
        // The conflicting counts must both be named and reconciled.
        cases.Add("haitian_creole_conflict", ["25", "27"]);
        return cases;
    }

    /// <summary>
    /// The same-conversation memory gate, in two success-required turns. The
    /// first turn must accept the stated facts under LEGEND authority and the
    /// follow-up must return the exact owner and closing date from the same
    /// conversation. A governed refusal fails this gate.
    /// </summary>
    [Fact]
    public async Task SameConversationMemory_RetainsAndReturnsTheExactValuesStatedEarlier()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        using var writeSentinel = new WriteAttemptSentinel();
        await using var db = BuildSentinelDb(writeSentinel);
        var founder = await AddFounderProfileAsync(db);
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        await AdmitFoundationPrerequisiteAsync(db);
        var handler = new RecordingProviderHandler();
        using var diagnosticCapture = new LegendFounderCurriculumSqlServerE2ETests.ExceptionCapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information).AddProvider(diagnosticCapture));
        var service = CreateService(db, handler, loggerFactory);
        var conversationId = Guid.NewGuid().ToString("D");

        writeSentinel.Arm();

        async Task<MatrixRow> SendAsync(string label, string prompt)
        {
            diagnosticCapture.ResetDiagnostics();
            var providerCallsBefore = handler.RequestCount;
            var providerClientsBefore = handler.ClientConstructions;
            var progress = new List<LegendFounderAiProgressEvent>();
            var response = await service.ReplyAsync(
                founder,
                new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    NativeOnly = true,
                    ConversationId = conversationId,
                    SourceLanguageCode = "en",
                    Messages = [new LegendFounderAiChatMessage("user", prompt)]
                },
                progress: (update, _) => { progress.Add(update); return ValueTask.CompletedTask; });

            return new MatrixRow(
                label,
                prompt,
                response.Succeeded,
                response.Message,
                response.Error,
                response.ResponseAuthority,
                response.Stage,
                response.Reason,
                ObservedEvidenceCount(response, progress),
                response.ModelProvenance,
                handler.RequestCount - providerCallsBefore,
                CompletedTools(progress),
                string.Empty,
                writeSentinel.OperationalWriteAttempts,
                writeSentinel.ObservedWriteEntities.ToArray(),
                db.ChangeTracker.Entries().Count(entry =>
                    entry.State != EntityState.Unchanged),
                response.EvidenceOrigin,
                handler.ClientConstructions - providerClientsBefore,
                progress.ToArray())
            {
                RuntimeDiagnostics = diagnosticCapture.SnapshotDiagnostics()
            };
        }

        var first = await SendAsync(
            "native_capability:same_conversation_memory_turn_one",
            MemoryFirstPrompt);

        // Force the follow-up to reload canonical persisted discourse state.
        // No assistant answer or expected value is injected into the request.
        db.ChangeTracker.Clear();
        service = CreateService(db, handler, loggerFactory);

        var followUp = await SendAsync(
            "native_capability:same_conversation_memory_turn_two",
            MemoryFollowUpPrompt);

        Record([first, followUp]);

        string[] canonicalDiscourseWrites =
        [
            "LegendFounderAiDiscourseConversation",
            "LegendFounderAiDiscourseTurn"
        ];

        AssertNativeCapability(first, ["Marlin"], canonicalDiscourseWrites);
        AssertNativeCapability(
            followUp,
            ["Corine", "November"],
            canonicalDiscourseWrites);
        Assert.Matches(@"(?i)\b(?:9|9th|ninth)\b", followUp.Message!);
    }

    /// <summary>
    /// The native tool-planning gate. Native-only operation must discover and
    /// execute the registered read-only governed tool itself, with zero
    /// provider participation, and bind the receipt's exact counts.
    /// </summary>
    [Fact]
    public async Task NativeOnlyToolPlanning_SelectsTheRegisteredReadToolAndBindsItsReceipt()
    {
        var row = await RunAsync(
            "native_capability:tool_planning",
            ToolPrompt,
            nativeOnly: true,
            seedOperationalRecords: true);

        Record([row]);

        AssertNativeCapability(row, ["client", "lead"]);
        Assert.Matches(@"(?i)(?:\b(?:1|one)\s+(?:active\s+)?clients?\b|\bclients?\s*[:=]\s*(?:1|one)\b)", row.Message!);
        Assert.Matches(@"(?i)(?:\b(?:1|one)\s+(?:active\s+)?leads?\b|\bleads?\s*[:=]\s*(?:1|one)\b)", row.Message!);
        Assert.Contains("legend_client_lead_portfolio", row.ToolCalls);
    }

    private static bool IsAnswered(MatrixRow row) =>
        row.Succeeded &&
        string.Equals(row.ResponseAuthority, "LegendAi", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(row.Message);

    /// <summary>
    /// The absolute native-only boundary: no provider HTTP call, no attempted
    /// operational write and no provider authority, whatever the outcome.
    /// </summary>
    private static void AssertNativeBoundary(
        MatrixRow row,
        IReadOnlyList<string>? allowedWriteEntities = null)
    {
        Assert.Equal(0, row.ProviderCalls);
        Assert.Equal(0, row.ProviderClientConstructions);
        Assert.Equal(0, row.OperationalWriteAttempts);
        Assert.NotEqual("OpenAITeacher", row.ResponseAuthority);

        // Zero-write is proven at three independent boundaries: no rejected
        // operational write, no persistence attempt outside the explicitly
        // bounded set under test, and no pending tracked mutation left behind.
        var allowed = allowedWriteEntities ?? [];

        Assert.Empty(
            row.ObservedWriteEntities
                .Where(entity => !allowed.Contains(entity, StringComparer.Ordinal)));
        Assert.Equal(0, row.PendingTrackedChanges);
    }

    /// <summary>
    /// Success-required native capability. There is no refusal alternative:
    /// the turn must succeed under LEGEND authority with the exact semantic
    /// elements of the correct answer, non-provider provenance, and a fully
    /// clean native boundary.
    /// </summary>
    private static void AssertNativeCapability(
        MatrixRow row,
        IReadOnlyList<string> requiredAnswerElements,
        IReadOnlyList<string>? allowedWriteEntities = null)
    {
        AssertNativeBoundary(row, allowedWriteEntities);
        Assert.True(
            IsAnswered(row),
            $"Native capability required. Label={row.Label}; stage={row.Stage}; reason={row.Reason}; error={row.Error}; message={row.Message}");
        Assert.Equal("LegendAi", row.ResponseAuthority);
        Assert.Equal(LegendConnectResearchEvidenceOrigin.InternalKnowledge, row.EvidenceOrigin);
        Assert.True(row.EvidenceCount > 0, "A native capability answer requires positive governed evidence lineage.");
        Assert.False(string.IsNullOrWhiteSpace(row.Message));
        Assert.DoesNotContain(
            "OpenAI",
            row.ModelProvenance ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        foreach (var element in requiredAnswerElements)
        {
            Assert.Contains(element, row.Message!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// An unanswered native turn must be an explicit, attributable governed
    /// refusal - never an empty or provider-attributed result.
    /// </summary>
    private static void AssertGovernedRefusal(MatrixRow row)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(row.Reason),
            $"An unanswered native-only turn must declare a governed reason. Label={row.Label}; stage={row.Stage}; message={row.Message}");
        Assert.NotEqual("LegendAi", row.ResponseAuthority);
    }

    [Fact]
    public async Task PermittedEscalationAndDirectProviderModeAreAttributedToTheProvider()
    {
        var escalated = await RunAsync(
            "escalation_allowed:deduction",
            DeductionPrompt,
            nativeOnly: false,
            providerText: "Kestrel contradicts the audited-file rule, so one of the two records must be false.");

        var direct = await RunAsync(
            "direct_provider:deduction",
            DeductionPrompt,
            nativeOnly: false,
            mode: "teacher",
            providerText: "The premises cannot both hold for one file.");

        Record([escalated, direct]);

        Assert.True(escalated.Succeeded, escalated.Error);
        Assert.Equal("OpenAITeacher", escalated.ResponseAuthority);
        Assert.Equal(1, escalated.ProviderCalls);
        Assert.Equal(0, escalated.OperationalWriteAttempts);
        Assert.True(direct.Succeeded, direct.Error);
        Assert.Equal("OpenAITeacher", direct.ResponseAuthority);
        Assert.Equal(1, direct.ProviderCalls);
    }

    /// <summary>
    /// Provider-loop receipt enforcement over held-out paraphrases of an
    /// owned-record request. The provider function call is scripted, so this
    /// is NOT evidence that LEGEND or the provider autonomously selects the
    /// tool, and it is not evidence of native-only tool planning, which
    /// remains unimplemented. What it proves is bounded and exact: the request
    /// routes to the governed read path, the registered tool executes against
    /// the authenticated database, the receipt carries the canonical counts,
    /// and the answer delivered to the Founder carries those same counts.
    /// </summary>
    [Theory]
    [InlineData("How many client records and how many leads do we have right now?")]
    [InlineData("What is the current count of our leads?")]
    [InlineData("Show me the status of our lead records today.")]
    public async Task ProviderLoopReceiptEnforcement_ExecutesTheRegisteredReadToolAndBindsItsExactCounts(
        string prompt)
    {
        var row = await RunAsync(
            "escalation_allowed:governed_tool_read",
            prompt,
            nativeOnly: false,
            providerResponses:
            [
                ProviderTool("legend_client_lead_portfolio", "{}"),
                ProviderText(
                    "Read-only inspection of legend_client_lead_portfolio returned 1 active client and 1 active lead.")
            ],
            seedOperationalRecords: true);

        Record([row]);

        Assert.True(row.Succeeded, row.Error);
        Assert.Equal(2, row.ProviderCalls);
        Assert.Contains("legend_client_lead_portfolio", row.ToolCalls);
        Assert.Contains(
            "\"activeLeadCount\":1",
            row.ReceiptRoundRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"websiteLeadCount\":0",
            row.ReceiptRoundRequest,
            StringComparison.Ordinal);
        Assert.Equal(0, row.OperationalWriteAttempts);

        // The delivered answer must carry the receipt's exact values and name
        // the receipt that authorized them; an unbound answer is not proof.
        Assert.NotNull(row.Message);
        Assert.Contains("legend_client_lead_portfolio", row.Message!, StringComparison.Ordinal);
        Assert.Contains("1 active client", row.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 active lead", row.Message!, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly (string Category, string Prompt)[] CapabilityMatrix =
    [
        ("arithmetic", ArithmeticPrompt),
        ("rewriting", RewritingPrompt),
        ("deduction", DeductionPrompt),
        ("causal_diagnosis", CausalPrompt),
        ("constrained_planning", PlanningPrompt),
        ("internal_data_uncertainty", UncertaintyPrompt),
        ("same_conversation_memory", MemoryFirstPrompt),
        ("haitian_creole_conflict", CreolePrompt)
    ];

    private static async Task<MatrixRow> RunAsync(
        string label,
        string prompt,
        bool nativeOnly,
        string mode = "legend",
        string? providerText = null,
        HttpResponseMessage[]? providerResponses = null,
        IReadOnlyList<LegendFounderAiChatMessage>? priorTurns = null,
        bool seedOperationalRecords = false,
        string? conversationId = null,
        string? sourceLanguageCode = "en")
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        using var writeSentinel = new WriteAttemptSentinel();
        await using var db = BuildSentinelDb(writeSentinel);
        var founder = await AddFounderProfileAsync(db);
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        if (seedOperationalRecords)
        {
            await SeedOperationalRecordsAsync(db);
        }

        if (label.StartsWith("native_capability:", StringComparison.Ordinal) ||
            label.StartsWith("native_only:", StringComparison.Ordinal))
            await AdmitFoundationPrerequisiteAsync(db);

        // Every persistence attempt from this point on is counted and rejected,
        // so zero writes is proven at the command boundary instead of inferred
        // from unchanged row counts.
        writeSentinel.Arm();
        var responses = providerResponses
            ?? (providerText is null ? [] : new[] { ProviderText(providerText) });
        var handler = new RecordingProviderHandler(responses);
        using var diagnosticCapture = new LegendFounderCurriculumSqlServerE2ETests.ExceptionCapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information).AddProvider(diagnosticCapture));
        var service = CreateService(db, handler, loggerFactory);

        var messages = new List<LegendFounderAiChatMessage>(
            priorTurns ?? []) { new("user", prompt) };

        var progress = new List<LegendFounderAiProgressEvent>();
        diagnosticCapture.ResetDiagnostics();
        var response = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest
            {
                Mode = mode,
                NativeOnly = nativeOnly,
                ConversationId = conversationId,
                SourceLanguageCode = sourceLanguageCode,
                Messages = messages
            },
            progress: (update, _) => { progress.Add(update); return ValueTask.CompletedTask; });

        return new MatrixRow(
            label,
            prompt,
            response.Succeeded,
            response.Message,
            response.Error,
            response.ResponseAuthority,
            response.Stage,
            response.Reason,
            ObservedEvidenceCount(response, progress),
            response.ModelProvenance,
            handler.RequestCount,
            CompletedTools(progress),
            handler.RequestBodies.Count > 1
                ? handler.RequestBodies[^1].Replace(
                    "\\u0022",
                    "\"",
                    StringComparison.Ordinal)
                : string.Empty,
            writeSentinel.OperationalWriteAttempts,
            writeSentinel.ObservedWriteEntities.ToList(),
            db.ChangeTracker.Entries().Count(entry =>
                entry.State != EntityState.Unchanged),
            response.EvidenceOrigin,
            handler.ClientConstructions,
            progress.ToArray())
        {
            RuntimeDiagnostics = diagnosticCapture.SnapshotDiagnostics()
        };
    }

    private static IReadOnlyList<string> CompletedTools(IEnumerable<LegendFounderAiProgressEvent> progress) =>
        progress.Where(update => update.Stage == "tool_complete" && !string.IsNullOrWhiteSpace(update.Tool))
            .Select(update => update.Tool!).ToArray();

    private static int ObservedEvidenceCount(
        LegendFounderAiChatResponse response,
        IEnumerable<LegendFounderAiProgressEvent> progress)
    {
        if (response.ResearchOutcome is not null)
            return response.ResearchOutcome.Session.ClaimEvidence.Count;
        // Preserve the native authority's own progress receipt. Model
        // provenance is optional for symbolic reasoning; positive governed
        // evidence and InternalKnowledge authority are still mandatory.
        var receipt = progress.LastOrDefault(update => update.Stage == "native_response");
        var match = Regex.Match(receipt?.Message ?? string.Empty,
            @"Answered from (?<count>\d+) governed LEGEND evidence record\(s\)");
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
    }

    private static void Record(IReadOnlyList<MatrixRow> rows)
    {
        var path = Environment.GetEnvironmentVariable(
            "LEGEND_HELDOUT_MATRIX_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            rows.Select(row => JsonSerializer.Serialize(row)));
        lock (RecordLock)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }

    private static readonly object RecordLock = new();

    private sealed record MatrixRow(
        string Label,
        string Prompt,
        bool Succeeded,
        string? Message,
        string? Error,
        string ResponseAuthority,
        string? Stage,
        string? Reason,
        int EvidenceCount,
        string? ModelProvenance,
        int ProviderCalls,
        IReadOnlyList<string> ToolCalls,
        string ReceiptRoundRequest,
        int OperationalWriteAttempts,
        IReadOnlyList<string> ObservedWriteEntities,
        int PendingTrackedChanges,
        LegendConnectResearchEvidenceOrigin EvidenceOrigin,
        int ProviderClientConstructions,
        IReadOnlyList<LegendFounderAiProgressEvent> Progress)
    {
        public LegendFounderCurriculumSqlServerE2ETests.RuntimeDiagnosticSnapshot? RuntimeDiagnostics { get; init; }
    }


    private static async Task SeedOperationalRecordsAsync(MasterAppDbContext db)
    {
        var client = new ClientProfile
        {
            ClientUserId = "held-out-client-1",
            FirstName = "Governed",
            LastName = "Client",
            CrmStatus = "Active",
            CrmNotes = "{\"recordType\":\"Client\"}"
        };
        db.ClientProfiles.Add(client);
        db.ClientEntitlements.Add(new ClientEntitlement
        {
            ClientProfileId = client.Id,
            Status = Domain.Billing.ClientEntitlementStatus.Active
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = FounderEnvironmentScope.FounderId,
            ClientUserId = "held-out-client-1"
        });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "held-out-lead-1",
            AgentUserId = FounderEnvironmentScope.FounderId,
            FirstName = "Held",
            LastName = "Out",
            Email = "held.out@legend.test",
            Phone = "0000000000",
            CrmStatus = "Lead"
        });
        await db.SaveChangesAsync();
    }

    private static Task AdmitFoundationPrerequisiteAsync(MasterAppDbContext db) =>
        LegendHeldOutFoundationPrerequisite.AdmitAsync(db,
            CapabilityMatrix.Select(item => item.Prompt)
                .Concat(new[] { MemoryFirstPrompt, MemoryFollowUpPrompt, ToolPrompt }).ToArray());

    private static LegendFounderAiConversationService CreateService(
        MasterAppDbContext db,
        RecordingProviderHandler handler,
        ILoggerFactory loggerFactory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "ht",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Haitian Creole",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Kreyòl ayisyen"
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            loggerFactory.CreateLogger<LegendConnectCorpusService>(),
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus,
            logger: loggerFactory.CreateLogger<LegendConnectCurriculumService>());
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            intelligence: intelligence);
        var accessResolver = new AgentProfileAccessResolver(db);

        return new LegendFounderAiConversationService(
            new RecordingHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-only-key",
                    ["OpenAI:LegendFounderAiTimeoutSeconds"] = "45"
                })
                .Build(),
            new FounderLegendConnectService(operations, accessResolver),
            loggerFactory.CreateLogger<LegendFounderAiConversationService>(),
            new LegendFounderAiDiscourseStateService(db, accessResolver, operations),
            registry,
            ControllerTestHelpers.BuildTranslationService(),
            softwareRemediation: null,
            agencyCommand: new AgencyCommandService(
                db,
                new ProductionService(db, loggerFactory.CreateLogger<ProductionService>()),
                loggerFactory.CreateLogger<AgencyCommandService>()));
    }

    private static async Task<ClaimsPrincipal> AddFounderProfileAsync(
        MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = FounderEnvironmentScope.FounderId,
            AgentUpn = "held-out-founder@legend.test",
            NormalizedEmail = "held-out-founder@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(FounderEnvironmentScope.FounderId);
    }

    private static MasterAppDbContext BuildSentinelDb(
        WriteAttemptSentinel sentinel) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(sentinel)
            .Options);

    /// <summary>
    /// Observes every persistence attempt on the read path. Any attempt that
    /// touches an operational record set is counted and rejected, so zero
    /// operational writes is proven at the persistence boundary instead of
    /// inferred from unchanged row counts. Other persistence attempts (the
    /// language registry provisioning its governed baseline) are recorded by
    /// entity name rather than hidden.
    /// </summary>
    private sealed class WriteAttemptSentinel : SaveChangesInterceptor, IDisposable
    {
        private static readonly string[] OperationalEntities =
        [
            nameof(ClientProfile),
            nameof(ClientEntitlement),
            nameof(AgentClient),
            nameof(WorkstationLeadProfile),
            nameof(WebsiteLead)
        ];

        private bool _armed;

        public int OperationalWriteAttempts { get; private set; }

        public SortedSet<string> ObservedWriteEntities { get; } =
            new(StringComparer.Ordinal);

        public void Arm() => _armed = true;

        public void Dispose() => _armed = false;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Observe(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void Observe(DbContextEventData eventData)
        {
            if (!_armed || eventData.Context is null)
                return;

            var entities = eventData.Context.ChangeTracker
                .Entries()
                .Where(entry => entry.State != EntityState.Unchanged &&
                                entry.State != EntityState.Detached)
                .Select(entry => entry.Entity.GetType().Name)
                .ToList();

            foreach (var entity in entities)
                ObservedWriteEntities.Add(entity);

            var operational = entities
                .Where(entity => OperationalEntities.Contains(entity))
                .ToList();

            if (operational.Count == 0)
                return;

            OperationalWriteAttempts += operational.Count;
            throw new InvalidOperationException(
                "A governed read path attempted to persist operational records: " +
                string.Join(", ", operational));
        }
    }

    private static HttpResponseMessage ProviderText(string text) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text } }
                }
            }
        });

    private static HttpResponseMessage ProviderTool(
        string name,
        string arguments) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "function_call",
                    call_id = "held-out-tool-call",
                    name,
                    arguments
                }
            }
        });

    private static HttpResponseMessage ProviderResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class RecordingProviderHandler(
        params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        public int ClientConstructions { get; set; }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "A provider call was made without a queued response.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed class RecordingHttpClientFactory(
        RecordingProviderHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            handler.ClientConstructions++;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.openai.com/")
            };
        }
    }

    private sealed class FounderEnvironmentScope : IDisposable
    {
        public const string FounderId = "3f0d6de5-9d3b-4d3a-8f6f-2f8f0d6cbf41";

        private readonly string? _previousFounderOid =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        public FounderEnvironmentScope() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(
                "FOUNDER_OID",
                _previousFounderOid);
    }
}
