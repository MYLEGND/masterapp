using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class Legend120x120SequentialTrainingTests
{
    private readonly ITestOutputHelper _output;
    public Legend120x120SequentialTrainingTests(ITestOutputHelper output) => _output = output;

    private sealed record Template(string Text, string FunctionSpan, string IntentSpan);
    private sealed record Capability(
        string Key,
        string Category,
        string SourceFunction,
        string SourceIntent,
        string ResultFunction,
        string ResultIntent,
        IReadOnlyList<Template> Sources,
        IReadOnlyList<Template> Results);
    private sealed record Stage(string Key, string Prefix, string ResultPrefix, string Purpose);

    private static readonly string[] Domains =
    [
        "data_migration", "technology_choice", "incident_report", "research_project", "product_launch",
        "system_failure", "process_delay", "market_assessment", "strategic_risk", "learning_iteration",
        "operations_change", "performance_decline", "policy_option", "resource_allocation", "research_finding",
        "research_summary", "executive_brief", "technical_explanation", "calculation_result", "instruction_execution",
        "investigation_plan", "system_growth", "software_release", "factual_claim", "conditional_case",
        "resource_limit", "rule_application", "quantity_relationship", "audience_scope", "reference_target"
    ];

    private static readonly Stage[] Stages =
    [
        new("direct", "", "", "Direct natural language with strong semantic grounding"),
        new("conversational", "Right now, ", "Right now, ", "Natural conversational phrasing and immediacy"),
        new("precommit", "Before we commit to action, ", "Before action is committed, ", "Pre-action reasoning and explicit decision discipline"),
        new("pressure", "Even under time pressure, ", "Under time pressure, ", "Robust meaning under urgency without lowering evidence standards"),
        new("incomplete", "Given incomplete evidence, ", "With incomplete evidence, ", "Uncertainty-aware understanding and calibrated responses"),
        new("constraint_change", "After the latest constraint change, ", "After the constraint change, ", "Revision after material context change"),
        new("assumption_challenge", "Now that a prior assumption is in doubt, ", "With the prior assumption under challenge, ", "Reevaluation when prior premises weaken"),
        new("followup", "Following the earlier discussion, ", "Following the earlier discussion, ", "Cross-turn continuation and discourse robustness"),
        new("transfer", "Applying the same reasoning in this new setting, ", "In the new setting, ", "Cross-context transfer of governed meaning"),
        new("highstakes", "Before an irreversible decision, ", "Before an irreversible decision, ", "High-stakes precision, reversibility, and verification"),
        new("ambiguity", "With ambiguity and conflicting signals still present, ", "With ambiguity and conflicting signals present, ", "Integrated ambiguity, conflict, and fail-closed reasoning"),
        new("integrated", "After combining the prior evidence, constraints, and context, ", "After integrating the prior evidence, constraints, and context, ", "Integrated multi-signal reasoning and articulation")
    ];

    [Fact]
    public async Task SubmitBatches002Through120Sequentially_AfterBatch001DurableAcceptance()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_CONNECTION");
        var founderOid = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_FOUNDER_OID");
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.False(string.IsNullOrWhiteSpace(founderOid));

        var cs = new SqlConnectionStringBuilder(raw!) { ApplicationName = "LEGEND 120x120 sequential Founder training" };
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(cs.ConnectionString).Options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:ContextualComposition:Mode"] = "Shadow"
            }).Build();

        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum, operations: null);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum, founderTrainingIngestion: founderTraining);
        var founderLegend = new FounderLegendConnectService(operations, new AgentProfileAccessResolver(db));
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderOid!)], "legend-120x120-founder"));

        Assert.True(await db.AgentProfiles.AsNoTracking().AnyAsync(x => x.IsActive && x.AgentUserId != null && x.AgentUserId.ToLower() == founderOid!.ToLower()));

        // User-directed sequencing gate: Batch 2 may begin as soon as Batch 1 is durably accepted.
        var batch1Deadline = DateTime.UtcNow.AddMinutes(12);
        LegendCurriculumManifestWorkItem? batch1 = null;
        while (DateTime.UtcNow < batch1Deadline)
        {
            db.ChangeTracker.Clear();
            batch1 = await db.Set<LegendCurriculumManifestWorkItem>().AsNoTracking()
                .Where(item => item.FounderUserId == founderOid && item.FamilyCount == 12 && item.ExampleCount == 120 &&
                    item.PayloadJson.Contains("legend.maxintent.b001.request.dm_request"))
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefaultAsync();
            if (batch1 is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(batch1);
        _output.WriteLine($"BATCH 001 DURABLE ACCEPTANCE CONFIRMED work={batch1!.Id} state={batch1.ProcessingState}");

        var capabilities = Capabilities();
        Assert.Equal(10, capabilities.Count);
        Assert.Equal(11, Stages.Length - 1); // Batch 120 uses integrated stage specially.

        var globalTexts = new HashSet<string>(StringComparer.Ordinal);
        var accepted = 1;

        for (var batchNumber = 2; batchNumber <= 120; batchNumber++)
        {
            var capabilityIndex = batchNumber == 120 ? 9 : (batchNumber - 2) % capabilities.Count;
            var stageIndex = batchNumber == 120 ? 11 : Math.Min(10, (batchNumber - 2) / capabilities.Count);
            var capability = capabilities[capabilityIndex];
            var stage = Stages[stageIndex];
            var manifest = BuildManifest(batchNumber, capability, stage, globalTexts);

            Assert.Equal(10, manifest.Families.Count);
            Assert.Equal(120, manifest.Families.Sum(f => f.Examples.Count));
            Assert.All(manifest.Families, family => Assert.Equal(12, family.Examples.Count));

            var firstKey = manifest.Families[0].FamilyKey;
            var lastKey = manifest.Families[^1].FamilyKey;

            var result = await founderLegend.QueueFounderCurriculumAsync(founder, manifest);
            _output.WriteLine($"BATCH {batchNumber:000} SUBMIT succeeded={result.Succeeded} duplicate={result.DuplicatePrevented} examples={result.EnglishExampleCount} error={result.ErrorCode ?? "<none>"}");
            _output.WriteLine($"BATCH {batchNumber:000} MESSAGE {result.Message}");

            Assert.True(result.Succeeded, $"Batch {batchNumber:000} rejected: {result.ErrorCode} {result.Message}");
            Assert.Equal(120, result.EnglishExampleCount);

            db.ChangeTracker.Clear();
            var durable = await db.Set<LegendCurriculumManifestWorkItem>().AsNoTracking()
                .Where(item => item.FounderUserId == founderOid && item.ExampleCount == 120 && item.FamilyCount == 10 &&
                    item.PayloadJson.Contains(firstKey) && item.PayloadJson.Contains(lastKey))
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefaultAsync();
            Assert.NotNull(durable);

            accepted++;
            _output.WriteLine($"BATCH {batchNumber:000} ACCEPTED work={durable!.Id} state={durable.ProcessingState} stage={stage.Key} capability={capability.Key}");
            await Task.Delay(150);
        }

        Assert.Equal(120, accepted);
        _output.WriteLine("============================================================");
        _output.WriteLine("LEGEND 120x120 SEQUENTIAL FOUNDER ACCEPTANCE PASS");
        _output.WriteLine("BATCHES DURABLY ACCEPTED: 120/120");
        _output.WriteLine("REQUESTED EXAMPLES: 14,400");
        _output.WriteLine("BACKGROUND PROCESSING WAS NOT USED AS AN INTER-BATCH GATE");
        _output.WriteLine("============================================================");
    }

    private static LegendConnectCurriculumManifestSubmission BuildManifest(
        int batchNumber,
        Capability capability,
        Stage stage,
        HashSet<string> globalTexts)
    {
        var start = ((batchNumber - 2) * 7) % Domains.Length;
        var domains = Enumerable.Range(0, 10).Select(i => Domains[(start + i * 3) % Domains.Length]).Distinct().ToArray();
        if (domains.Length != 10)
        {
            domains = Enumerable.Range(0, 10).Select(i => Domains[(start + i) % Domains.Length]).ToArray();
        }

        var families = new List<LegendConnectCurriculumBatchSubmission>(10);
        for (var familyIndex = 0; familyIndex < 10; familyIndex++)
        {
            var domain = domains[familyIndex];
            var surfaceDomain = domain.Replace('_', ' ');
            var familyKey = $"legend.maxintent.b{batchNumber:000}.{stage.Key}.{capability.Key}.f{familyIndex + 1:00}.{domain}";
            var examples = new List<LegendConnectCurriculumExampleSubmission>(12);

            for (var i = 0; i < capability.Sources.Count; i++)
            {
                var template = capability.Sources[i];
                var core = template.Text.Replace("{d}", surfaceDomain, StringComparison.Ordinal);
                var text = ApplyPrefix(stage.Prefix, core);
                AddExample(examples, globalTexts, batchNumber, familyIndex, i, false, familyKey, domain, capability.SourceFunction, capability.SourceIntent, text, template.FunctionSpan, template.IntentSpan);
            }
            for (var i = 0; i < capability.Results.Count; i++)
            {
                var template = capability.Results[i];
                var core = template.Text.Replace("{d}", surfaceDomain, StringComparison.Ordinal);
                var text = ApplyPrefix(stage.ResultPrefix, core);
                AddExample(examples, globalTexts, batchNumber, familyIndex, i, true, familyKey, domain, capability.ResultFunction, capability.ResultIntent, text, template.FunctionSpan, template.IntentSpan);
            }

            var transition = new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = capability.SourceFunction,
                    ["intent"] = capability.SourceIntent,
                    ["domain_context"] = "$domain_context"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = capability.ResultFunction,
                    ["intent"] = capability.ResultIntent,
                    ["domain_context"] = "$domain_context"
                }));

            families.Add(new LegendConnectCurriculumBatchSubmission(
                familyKey,
                $"{stage.Purpose}: {capability.Category}",
                examples,
                [transition]));
        }

        return new LegendConnectCurriculumManifestSubmission(families);
    }

    private static void AddExample(
        List<LegendConnectCurriculumExampleSubmission> examples,
        HashSet<string> globalTexts,
        int batchNumber,
        int familyIndex,
        int templateIndex,
        bool result,
        string familyKey,
        string domain,
        string function,
        string intent,
        string text,
        string functionSpan,
        string intentSpan)
    {
        var surfaceDomain = domain.Replace('_', ' ');
        Assert.Contains(functionSpan, text, StringComparison.Ordinal);
        Assert.Contains(intentSpan, text, StringComparison.Ordinal);
        Assert.Contains(surfaceDomain, text, StringComparison.OrdinalIgnoreCase);
        Assert.True(globalTexts.Add(text), $"Duplicate generated curriculum text: {text}");

        var graph = new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("function", "conversation_function", function, functionSpan),
                new LegendConnectMeaningNodeSubmission("intent", "intent", intent, intentSpan),
                new LegendConnectMeaningNodeSubmission("context", "domain_context", domain, surfaceDomain)
            ],
            [
                new LegendConnectMeaningRelationSubmission("function", "governs", "intent"),
                new LegendConnectMeaningRelationSubmission("function", "applies-to", "context")
            ]);

        examples.Add(new LegendConnectCurriculumExampleSubmission(
            text,
            new Dictionary<string, string>
            {
                ["conversation_function"] = function,
                ["intent"] = intent,
                ["domain_context"] = domain
            },
            graph,
            $"legend-120x120-b{batchNumber:000}-f{familyIndex + 1:00}-{(result ? "result" : "source")}-{templateIndex + 1:00}"));
    }

    private static string ApplyPrefix(string prefix, string core)
    {
        if (string.IsNullOrEmpty(prefix)) return core;
        if (core.StartsWith("I ", StringComparison.Ordinal)) return prefix + core;
        if (core.Length == 0) return prefix;
        return prefix + char.ToLowerInvariant(core[0]) + core[1..];
    }

    private static IReadOnlyList<Capability> Capabilities() =>
    [
        new("request", "Request interpretation: goal, constraints, dependencies, and completion criteria",
            "complex_request", "identify_goal_constraints", "request_interpretation", "state_goal_constraints",
            [
                T("Identify the required outcome, hard constraints, dependencies, and completion evidence for the {d}.", "Identify", "required outcome, hard constraints, dependencies, and completion evidence"),
                T("Separate the {d} goal from its boundaries, prerequisites, and success criteria.", "Separate", "goal from its boundaries, prerequisites, and success criteria"),
                T("What must the {d} achieve, preserve, depend on, and prove before action begins?", "What must", "achieve, preserve, depend on, and prove"),
                T("Clarify the objective, non-negotiables, required inputs, and finish condition for the {d}.", "Clarify", "objective, non-negotiables, required inputs, and finish condition"),
                T("Break the {d} request into intended outcome, governing limits, dependencies, and acceptance evidence.", "Break", "intended outcome, governing limits, dependencies, and acceptance evidence"),
                T("Before solving the {d}, determine what success requires, what cannot change, what must already be true, and what confirms completion.", "determine", "what success requires, what cannot change, what must already be true, and what confirms completion"),
                T("Make the {d} actionable by distinguishing its target outcome, fixed constraints, prerequisites, and completion test.", "distinguishing", "target outcome, fixed constraints, prerequisites, and completion test")
            ],
            [
                T("I will frame the {d} around its outcome, constraints, prerequisites, and success test.", "frame", "outcome, constraints, prerequisites, and success test"),
                T("The {d} becomes actionable when its objective, boundaries, dependencies, and completion criteria are explicit.", "becomes actionable", "objective, boundaries, dependencies, and completion criteria"),
                T("I will keep the {d} goal, non-negotiables, required inputs, and verification condition distinct.", "keep", "goal, non-negotiables, required inputs, and verification condition"),
                T("The {d} request is organized by what must be achieved, preserved, supplied, and verified.", "organized", "what must be achieved, preserved, supplied, and verified"),
                T("I will interpret the {d} by separating success, constraints, dependencies, and the finish line before recommending action.", "interpret", "success, constraints, dependencies, and the finish line")
            ]),

        new("diagnostic", "Causal diagnosis: competing causes, evidence, uncertainty, and discriminating tests",
            "diagnostic_request", "identify_cause_from_evidence", "diagnostic_response", "rank_and_test_causes",
            [
                T("Diagnose the {d} by comparing plausible causes with supporting and conflicting evidence.", "Diagnose", "plausible causes with supporting and conflicting evidence"),
                T("Which explanation best accounts for the {d}, and what observation would separate it from credible alternatives?", "Which explanation", "what observation would separate it from credible alternatives"),
                T("Rank the possible causes of the {d} by evidence before treating any one explanation as established.", "Rank", "by evidence before treating any one explanation as established"),
                T("For the {d}, identify competing hypotheses and choose the test that would most reduce uncertainty.", "identify", "competing hypotheses and choose the test that would most reduce uncertainty"),
                T("What evidence supports or conflicts with the leading explanation for the {d}, and what should be tested next?", "What evidence", "supports or conflicts with the leading explanation"),
                T("Investigate the {d} without assuming the cause; preserve alternatives until discriminating evidence separates them.", "Investigate", "preserve alternatives until discriminating evidence separates them"),
                T("Use the available evidence to narrow causes of the {d}, then select a test aimed at the strongest uncertainty.", "narrow", "select a test aimed at the strongest uncertainty")
            ],
            [
                T("I will rank plausible causes for the {d}, cite supporting and conflicting evidence, and choose a discriminating test.", "rank", "cite supporting and conflicting evidence, and choose a discriminating test"),
                T("The {d} diagnosis will remain provisional until evidence separates the leading explanation from credible alternatives.", "diagnosis", "remain provisional until evidence separates the leading explanation from credible alternatives"),
                T("I will compare hypotheses for the {d} against common evidence and prioritize the test that most reduces uncertainty.", "compare", "prioritize the test that most reduces uncertainty"),
                T("A defensible {d} diagnosis ranks competing causes before testing the distinction that matters most.", "ranks", "competing causes before testing the distinction that matters most"),
                T("For the {d}, I will preserve multiple explanations when the evidence does not yet justify collapsing to one cause.", "preserve", "multiple explanations when the evidence does not yet justify collapsing to one cause")
            ]),

        new("decision", "Decision reasoning: hard constraints, common criteria, tradeoffs, and reversal conditions",
            "decision_request", "compare_options_constraints", "decision_response", "recommend_with_tradeoffs",
            [
                T("Compare the {d} alternatives against the same criteria after applying all hard constraints.", "Compare", "alternatives against the same criteria after applying all hard constraints"),
                T("Which {d} option best satisfies the priorities without violating the non-negotiables?", "Which", "best satisfies the priorities without violating the non-negotiables"),
                T("Eliminate {d} choices that fail mandatory requirements before weighing benefits, risks, and tradeoffs.", "Eliminate", "choices that fail mandatory requirements before weighing benefits, risks, and tradeoffs"),
                T("Decide the {d} using common criteria, explicit tradeoffs, downside risk, and reversibility.", "Decide", "common criteria, explicit tradeoffs, downside risk, and reversibility"),
                T("What would make the strongest alternative for the {d} preferable to the current recommendation?", "What would", "preferable to the current recommendation"),
                T("Evaluate every feasible {d} option by the same priorities rather than by familiarity or preference.", "Evaluate", "by the same priorities rather than by familiarity or preference"),
                T("For the {d}, separate hard requirements from weighted preferences before ranking the viable options.", "separate", "hard requirements from weighted preferences before ranking the viable options")
            ],
            [
                T("I will compare feasible {d} options against common criteria, state tradeoffs, and recommend the best-supported fit.", "compare", "state tradeoffs, and recommend the best-supported fit"),
                T("The {d} recommendation will identify the decisive criteria, accepted disadvantages, and conditions that would reverse the choice.", "recommendation", "decisive criteria, accepted disadvantages, and conditions that would reverse the choice"),
                T("I will reject {d} options that violate mandatory constraints before ranking what remains.", "reject", "options that violate mandatory constraints before ranking what remains"),
                T("A defensible {d} decision applies the same priorities to every viable alternative and makes the accepted downside explicit.", "decision", "same priorities to every viable alternative and makes the accepted downside explicit"),
                T("For the {d}, I will state what evidence or changed constraint would make another option preferable.", "state", "what evidence or changed constraint would make another option preferable")
            ]),

        new("evidence", "Evidence calibration: facts, inference, estimates, assumptions, and unknowns",
            "evidence_question", "classify_epistemic_status", "evidence_calibrated_response", "label_fact_inference_unknown",
            [
                T("Classify the {d} evidence into confirmed facts, supported inferences, estimates, assumptions, and unknowns.", "Classify", "confirmed facts, supported inferences, estimates, assumptions, and unknowns"),
                T("What is actually established about the {d}, what follows from evidence, and what remains uncertain?", "What", "what follows from evidence, and what remains uncertain"),
                T("Separate {d} facts from deductions, estimates, assumptions, and missing evidence.", "Separate", "facts from deductions, estimates, assumptions, and missing evidence"),
                T("Before concluding about the {d}, label the epistemic status of every material claim.", "label", "epistemic status of every material claim"),
                T("Which claims about the {d} are observed, inferred, estimated, assumed, or unresolved?", "Which claims", "observed, inferred, estimated, assumed, or unresolved"),
                T("Do not treat every {d} statement as fact; distinguish what is known from what still needs evidence.", "distinguish", "what is known from what still needs evidence"),
                T("For the {d}, calibrate any conclusion to both evidence strength and unresolved uncertainty.", "calibrate", "conclusion to both evidence strength and unresolved uncertainty")
            ],
            [
                T("I will keep {d} facts, supported inferences, estimates, assumptions, and unknowns distinct.", "keep", "facts, supported inferences, estimates, assumptions, and unknowns distinct"),
                T("Any {d} conclusion will remain calibrated to the quality and completeness of its supporting evidence.", "calibrated", "quality and completeness of its supporting evidence"),
                T("I will state what is established about the {d}, what is inferred, and what remains unresolved.", "state", "what is inferred, and what remains unresolved"),
                T("The {d} analysis will not promote an assumption or estimate into a fact merely because it supports a preferred conclusion.", "analysis", "will not promote an assumption or estimate into a fact merely because it supports a preferred conclusion"),
                T("For the {d}, I will leave uncertainty visible until stronger evidence justifies changing its status.", "leave", "uncertainty visible until stronger evidence justifies changing its status")
            ]),

        new("planning", "Dependency-aware planning: prerequisites, sequencing, validation, and bounded recovery",
            "planning_request", "decompose_goal_dependencies", "ordered_plan_response", "sequence_actions_checks",
            [
                T("Build a {d} plan that orders prerequisites, parallel work, validation gates, and recovery actions.", "Build", "prerequisites, parallel work, validation gates, and recovery actions"),
                T("For the {d}, identify dependencies before sequencing actions and checkpoints.", "identify", "dependencies before sequencing actions and checkpoints"),
                T("What must happen first, what can run in parallel, and what must be verified during the {d}?", "What must", "happen first, what can run in parallel, and what must be verified"),
                T("Decompose the {d} into inputs, dependencies, outputs, and explicit verification conditions.", "Decompose", "inputs, dependencies, outputs, and explicit verification conditions"),
                T("Plan the {d} so failure is detected early and a bounded fallback exists before consequential transitions.", "Plan", "failure is detected early and a bounded fallback exists before consequential transitions"),
                T("Before executing the {d}, expose its critical path and place checkpoints before irreversible steps.", "expose", "critical path and place checkpoints before irreversible steps"),
                T("Sequence the {d} around acceptance criteria, dependencies, validation, and recovery.", "Sequence", "acceptance criteria, dependencies, validation, and recovery")
            ],
            [
                T("The {d} plan will order prerequisites, parallel work, validation gates, and recovery actions around the acceptance criteria.", "order", "prerequisites, parallel work, validation gates, and recovery actions around the acceptance criteria"),
                T("I will expose the {d} critical path and place verification checkpoints before consequential transitions.", "expose", "critical path and place verification checkpoints before consequential transitions"),
                T("For the {d}, each step will name its input, dependency, output, and verification condition.", "name", "input, dependency, output, and verification condition"),
                T("The {d} sequence will preserve constraints, detect failure early, and provide a bounded fallback.", "sequence", "preserve constraints, detect failure early, and provide a bounded fallback"),
                T("I will structure the {d} so dependencies are explicit and every major transition has a check or recovery path.", "structure", "dependencies are explicit and every major transition has a check or recovery path")
            ]),

        new("reasoning", "Supported reasoning: valid inference, explicit premises, and verifiable conclusions",
            "reasoning_request", "derive_supported_conclusion", "reasoned_answer", "explain_valid_inference",
            [
                T("Derive a conclusion about the {d} only from evidence and premises that actually support it.", "Derive", "only from evidence and premises that actually support it"),
                T("What conclusion about the {d} follows from the evidence without adding unsupported assumptions?", "What conclusion", "follows from the evidence without adding unsupported assumptions"),
                T("For the {d}, distinguish what the premises entail from what would merely be plausible.", "distinguish", "what the premises entail from what would merely be plausible"),
                T("Explain the {d} conclusion by tracing the valid inference from evidence to result.", "Explain", "valid inference from evidence to result"),
                T("Do the available premises justify the {d} conclusion, or is an unsupported step being introduced?", "justify", "or is an unsupported step being introduced"),
                T("Reason through the {d} without treating correlation, preference, or possibility as proof.", "Reason", "without treating correlation, preference, or possibility as proof"),
                T("For the {d}, preserve each inferential step so the conclusion can be challenged and verified.", "preserve", "each inferential step so the conclusion can be challenged and verified")
            ],
            [
                T("For the {d}, I will state the supported conclusion and explain the evidence-to-inference path that justifies it.", "state", "supported conclusion and explain the evidence-to-inference path that justifies it"),
                T("The {d} answer will separate valid inference from assumptions that the evidence does not establish.", "separate", "valid inference from assumptions that the evidence does not establish"),
                T("I will explain why the {d} conclusion follows and identify any premise on which it depends.", "explain", "conclusion follows and identify any premise on which it depends"),
                T("A defensible {d} conclusion keeps the reasoning chain explicit enough to verify or challenge.", "keeps", "reasoning chain explicit enough to verify or challenge"),
                T("For the {d}, I will not claim more than the governed premises and evidence support.", "claim", "more than the governed premises and evidence support")
            ]),

        new("synthesis", "Evidence synthesis: coherent conclusions without erasing conflict or uncertainty",
            "synthesis_request", "integrate_multiple_evidence", "synthesis_response", "organize_conclusion_support",
            [
                T("Synthesize the {d} by combining compatible evidence while preserving conflicts and unresolved questions.", "Synthesize", "compatible evidence while preserving conflicts and unresolved questions"),
                T("For the {d}, integrate the strongest evidence without erasing disagreement or uncertainty.", "integrate", "strongest evidence without erasing disagreement or uncertainty"),
                T("What conclusion emerges from the {d} when supporting evidence, conflicts, and unknowns are considered together?", "What conclusion", "supporting evidence, conflicts, and unknowns are considered together"),
                T("Organize the {d} around the conclusion, its supporting evidence, important conflicts, and remaining uncertainty.", "Organize", "conclusion, its supporting evidence, important conflicts, and remaining uncertainty"),
                T("Combine the {d} evidence into one coherent account without pretending incompatible claims agree.", "Combine", "evidence into one coherent account without pretending incompatible claims agree"),
                T("For the {d}, preserve the source of each major claim while compressing compatible evidence.", "preserve", "source of each major claim while compressing compatible evidence"),
                T("Build a concise {d} synthesis that leads with the conclusion but keeps limitations visible.", "Build", "synthesis that leads with the conclusion but keeps limitations visible")
            ],
            [
                T("The {d} synthesis will lead with the conclusion, group supporting evidence, preserve conflicts, and state unresolved questions.", "synthesis", "lead with the conclusion, group supporting evidence, preserve conflicts, and state unresolved questions"),
                T("I will keep each major {d} claim connected to the evidence that supports or limits it.", "keep", "claim connected to the evidence that supports or limits it"),
                T("For the {d}, I will combine compatible evidence without collapsing meaningful disagreement.", "combine", "compatible evidence without collapsing meaningful disagreement"),
                T("The {d} response will separate the main conclusion from supporting evidence, conflicts, and unresolved uncertainty.", "separate", "main conclusion from supporting evidence, conflicts, and unresolved uncertainty"),
                T("I will present the {d} as a coherent synthesis while preserving qualifications that materially affect the conclusion.", "present", "coherent synthesis while preserving qualifications that materially affect the conclusion")
            ]),

        new("correction", "Evidence-governed correction: preserve valid context, replace disproven claims, and verify",
            "correction_request", "detect_repair_error", "correction_response", "acknowledge_correct_verify",
            [
                T("A contradiction appears in the {d}; identify the specific error, preserve what remains valid, and replace only the disproven part.", "identify", "specific error, preserve what remains valid, and replace only the disproven part"),
                T("Correct the {d} using the governing evidence and verify the revised conclusion independently.", "Correct", "governing evidence and verify the revised conclusion independently"),
                T("Which part of the {d} is wrong, what evidence disproves it, and what should remain unchanged?", "Which part", "what evidence disproves it, and what should remain unchanged"),
                T("For the {d}, acknowledge the error before substituting the evidence-supported correction.", "acknowledge", "error before substituting the evidence-supported correction"),
                T("Repair the {d} without discarding valid context that the contradiction does not affect.", "Repair", "without discarding valid context that the contradiction does not affect"),
                T("What must change in the {d}, and what must be independently verified before the correction is complete?", "What must", "independently verified before the correction is complete"),
                T("When evidence contradicts the {d}, update the invalid conclusion while retaining supported facts.", "update", "invalid conclusion while retaining supported facts")
            ],
            [
                T("For the {d}, I will acknowledge the specific error, replace it with the evidence-supported correction, and verify the result.", "acknowledge", "specific error, replace it with the evidence-supported correction, and verify the result"),
                T("The {d} correction is complete only after the revised conclusion passes independent verification.", "correction", "revised conclusion passes independent verification"),
                T("I will preserve valid {d} context while replacing only the claim that the governing evidence disproves.", "preserve", "context while replacing only the claim that the governing evidence disproves"),
                T("For the {d}, I will state what changed, why it changed, and what evidence now supports the corrected result.", "state", "what changed, why it changed, and what evidence now supports the corrected result"),
                T("The corrected {d} answer will not retain a conclusion after its supporting premise has been invalidated.", "retain", "conclusion after its supporting premise has been invalidated")
            ]),

        new("forward", "Forward strategy: scenarios, assumptions, indicators, and adaptation triggers",
            "forward_reasoning_request", "anticipate_outcomes_adapt", "forward_strategy_response", "project_scenarios_adjust",
            [
                T("For the {d}, project likely, favorable, and adverse outcomes while stating the assumptions behind each scenario.", "project", "likely, favorable, and adverse outcomes while stating the assumptions behind each scenario"),
                T("What could happen next in the {d}, which signals matter early, and what should trigger adaptation?", "What could", "which signals matter early, and what should trigger adaptation"),
                T("Anticipate how the {d} may evolve under different assumptions and identify conditions that require a strategy change.", "Anticipate", "under different assumptions and identify conditions that require a strategy change"),
                T("For the {d}, compare plausible scenarios and define indicators that would move the plan from one response to another.", "compare", "plausible scenarios and define indicators that would move the plan from one response to another"),
                T("Do not make one forecast for the {d}; model a base case, upside case, downside case, and adaptation triggers.", "model", "base case, upside case, downside case, and adaptation triggers"),
                T("Identify the assumptions that drive the {d} outlook and the evidence thresholds that should change the plan.", "Identify", "evidence thresholds that should change the plan"),
                T("For the {d}, distinguish leading indicators from lagging outcomes so the strategy can adapt early.", "distinguish", "leading indicators from lagging outcomes so the strategy can adapt early")
            ],
            [
                T("For the {d}, I will state assumptions, compare plausible scenarios, watch leading indicators, and define adaptation triggers.", "state", "assumptions, compare plausible scenarios, watch leading indicators, and define adaptation triggers"),
                T("Each {d} outcome will become evidence for the next decision rather than a reason to defend the original plan.", "become", "evidence for the next decision rather than a reason to defend the original plan"),
                T("The {d} strategy will define what signals strengthen the current path and what thresholds require adjustment.", "define", "what signals strengthen the current path and what thresholds require adjustment"),
                T("I will compare likely, favorable, and adverse {d} scenarios and make the assumptions behind them explicit.", "compare", "scenarios and make the assumptions behind them explicit"),
                T("For the {d}, I will treat new evidence as a reason to update the strategy when predefined conditions are crossed.", "update", "strategy when predefined conditions are crossed")
            ]),

        new("clarification", "Material ambiguity: ask the smallest decisive question instead of guessing",
            "underspecified_request", "detect_material_ambiguity", "clarification_request", "ask_decisive_question",
            [
                T("The {d} request is missing information that could materially change the answer; identify the decisive uncertainty before proceeding.", "identify", "decisive uncertainty before proceeding"),
                T("What must be clarified about the {d} before a reliable answer can be given?", "What must", "before a reliable answer can be given"),
                T("For the {d}, distinguish harmless missing detail from ambiguity that changes the decision.", "distinguish", "harmless missing detail from ambiguity that changes the decision"),
                T("Ask the minimum question needed to resolve the material ambiguity in the {d}.", "Ask", "minimum question needed to resolve the material ambiguity"),
                T("Do not guess the missing {d} condition when different answers would lead to different outcomes.", "guess", "condition when different answers would lead to different outcomes"),
                T("Which unresolved detail in the {d} has the greatest effect on what should happen next?", "Which", "greatest effect on what should happen next"),
                T("Before answering the {d}, isolate the ambiguity that materially changes the reasoning path.", "isolate", "ambiguity that materially changes the reasoning path")
            ],
            [
                T("For the {d}, I will ask one focused question that resolves the uncertainty most likely to change the answer.", "ask", "focused question that resolves the uncertainty most likely to change the answer"),
                T("The {d} needs clarification only where missing information materially changes the conclusion or action.", "clarification", "missing information materially changes the conclusion or action"),
                T("I will not invent a {d} assumption when a single decisive question can establish the required context.", "invent", "assumption when a single decisive question can establish the required context"),
                T("For the {d}, I will separate nonessential missing detail from uncertainty that blocks a reliable answer.", "separate", "nonessential missing detail from uncertainty that blocks a reliable answer"),
                T("A useful {d} clarification targets the smallest unresolved point with the largest effect on the response.", "targets", "smallest unresolved point with the largest effect on the response")
            ])
    ];

    private static Template T(string text, string functionSpan, string intentSpan) => new(text, functionSpan, intentSpan);
}
