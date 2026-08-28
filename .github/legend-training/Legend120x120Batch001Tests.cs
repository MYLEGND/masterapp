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
public sealed class Legend120x120Batch001Tests
{
    private readonly ITestOutputHelper _output;
    public Legend120x120Batch001Tests(ITestOutputHelper output) => _output = output;

    private sealed record ExampleSpec(string Text, string FunctionSpan, string IntentSpan, bool IsResult);
    private sealed record FamilySpec(
        string Key,
        string Category,
        string Domain,
        string SourceFunction,
        string SourceIntent,
        string ResultFunction,
        string ResultIntent,
        IReadOnlyList<ExampleSpec> Examples);

    [Fact]
    public async Task Batch001_SubmitsThroughCanonicalFounderAuthority_AndCompletesWithoutContradiction()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_CONNECTION");
        var founderOid = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_FOUNDER_OID");
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.False(string.IsNullOrWhiteSpace(founderOid));

        var cs = new SqlConnectionStringBuilder(raw!)
        {
            ApplicationName = "LEGEND 120x120 Batch 001"
        };
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .Options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:ContextualComposition:Mode"] = "Shadow"
            })
            .Build();

        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(
            db, registry, corpus, curriculum, operations: null);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            founderTrainingIngestion: founderTraining);
        var profiles = new AgentProfileAccessResolver(db);
        var founderLegend = new FounderLegendConnectService(operations, profiles);
        var founder = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", founderOid!)], "legend-120x120-founder"));

        Assert.True(await db.AgentProfiles.AsNoTracking().AnyAsync(x =>
            x.IsActive && x.AgentUserId != null && x.AgentUserId.ToLower() == founderOid!.ToLower()));

        var specs = Batch001Specs();
        Assert.Equal(12, specs.Count);
        Assert.All(specs, family => Assert.Equal(10, family.Examples.Count));
        Assert.Equal(120, specs.Sum(family => family.Examples.Count));
        Assert.Equal(12, specs.Select(family => family.Key).Distinct(StringComparer.Ordinal).Count());

        var allTexts = specs.SelectMany(family => family.Examples).Select(example => example.Text).ToArray();
        Assert.Equal(120, allTexts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(specs.SelectMany(family => family.Examples.Select(example => (family, example))), pair =>
        {
            Assert.Contains(pair.example.FunctionSpan, pair.example.Text, StringComparison.Ordinal);
            Assert.Contains(pair.example.IntentSpan, pair.example.Text, StringComparison.Ordinal);
            Assert.Contains(pair.family.Domain.Replace('_', ' '), pair.example.Text, StringComparison.OrdinalIgnoreCase);
        });

        var familyKeys = specs.Select(family => family.Key).ToArray();
        var existingFamilyKeys = await db.LegendCurriculumFamilies.AsNoTracking()
            .Where(family => familyKeys.Contains(family.FamilyKey))
            .Select(family => family.FamilyKey)
            .ToListAsync();
        Assert.Empty(existingFamilyKeys);

        Assert.False(await db.LegendLanguageTextUnits.AsNoTracking().AnyAsync(unit =>
            unit.LanguageCode == "en" && unit.IsTrainingEligible && allTexts.Contains(unit.Text)));

        var manifest = new LegendConnectCurriculumManifestSubmission(
            specs.Select(BuildFamily).ToArray());

        _output.WriteLine("=== BATCH 001 PREFLIGHT ===");
        _output.WriteLine("families=12 examples=120 source=72 result=48");
        foreach (var family in specs)
            _output.WriteLine($"FAMILY {family.Key} domain={family.Domain} source={family.SourceFunction}/{family.SourceIntent} result={family.ResultFunction}/{family.ResultIntent}");

        var submittedAt = DateTime.UtcNow;
        var result = await founderLegend.QueueFounderCurriculumAsync(founder, manifest);
        _output.WriteLine("=== CANONICAL FOUNDER SUBMISSION RESULT ===");
        _output.WriteLine($"succeeded={result.Succeeded} duplicate={result.DuplicatePrevented} error={result.ErrorCode ?? "<none>"}");
        _output.WriteLine($"message={result.Message}");
        _output.WriteLine($"englishExamples={result.EnglishExampleCount}");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(120, result.EnglishExampleCount);
        Assert.Contains("durably queued", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var work = await FindWorkAsync(db, founderOid!, submittedAt);
        Assert.NotNull(work);
        _output.WriteLine($"WORK id={work!.Id} state={work.ProcessingState} families={work.FamilyCount} examples={work.ExampleCount} nextFamily={work.NextFamilyIndex}");
        Assert.Equal(12, work.FamilyCount);
        Assert.Equal(120, work.ExampleCount);

        var deadline = DateTime.UtcNow.AddMinutes(18);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            db.ChangeTracker.Clear();
            work = await db.Set<LegendCurriculumManifestWorkItem>().AsNoTracking()
                .SingleAsync(item => item.Id == work!.Id);
            _output.WriteLine($"WORK state={work.ProcessingState} nextFamily={work.NextFamilyIndex}/12");
            if (string.Equals(work.ProcessingState, "Completed", StringComparison.OrdinalIgnoreCase))
                break;
            if (string.Equals(work.ProcessingState, "Failed", StringComparison.OrdinalIgnoreCase))
                break;
        }

        Assert.Equal("Completed", work!.ProcessingState);
        Assert.Equal(12, work.NextFamilyIndex);

        db.ChangeTracker.Clear();
        var families = await db.LegendCurriculumFamilies.AsNoTracking()
            .Where(family => familyKeys.Contains(family.FamilyKey))
            .ToListAsync();
        Assert.Equal(12, families.Count);
        var familyIds = families.Select(family => family.Id).ToArray();
        var examples = await db.LegendCurriculumExamples.AsNoTracking()
            .Where(example => familyIds.Contains(example.CurriculumFamilyId) &&
                example.LanguageCode == "en" && example.SupersededUtc == null)
            .ToListAsync();
        Assert.Equal(120, examples.Count);
        Assert.All(familyIds, familyId => Assert.Equal(10, examples.Count(example => example.CurriculumFamilyId == familyId)));

        var semanticKeys = examples.Select(example => example.SemanticExampleIdentity).Where(key => key != null).ToArray();
        Assert.Equal(120, semanticKeys.Length);
        Assert.Equal(120, semanticKeys.Distinct(StringComparer.Ordinal).Count());

        var exampleIds = examples.Select(example => example.Id).ToArray();
        var nodeCount = await db.Set<LegendLanguageMeaningNodeEvidence>().AsNoTracking()
            .CountAsync(node => exampleIds.Contains(node.CurriculumExampleId) && node.SupersededUtc == null);
        var relationCount = await db.Set<LegendLanguageMeaningRelationEvidence>().AsNoTracking()
            .CountAsync(edge => exampleIds.Contains(edge.CurriculumExampleId) && edge.SupersededUtc == null);
        Assert.Equal(360, nodeCount);
        Assert.Equal(240, relationCount);

        var expected = specs
            .GroupBy(spec => new { spec.SourceFunction, spec.SourceIntent, spec.ResultFunction, spec.ResultIntent })
            .Select(group => new
            {
                Source = CanonicalFrame(group.Key.SourceFunction, group.Key.SourceIntent),
                Result = CanonicalFrame(group.Key.ResultFunction, group.Key.ResultIntent),
                FamilyKeys = group.Select(spec => spec.Key).ToArray()
            })
            .ToArray();
        Assert.Equal(4, expected.Length);

        foreach (var transition in expected)
        {
            var groupFamilyIds = families.Where(f => transition.FamilyKeys.Contains(f.FamilyKey)).Select(f => f.Id).ToArray();
            var groupExampleIds = examples.Where(e => groupFamilyIds.Contains(e.CurriculumFamilyId)).Select(e => e.Id).ToArray();
            var evidence = await db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
                .Where(item => item.SupersededUtc == null && groupExampleIds.Contains(item.SourceCurriculumExampleId) &&
                    item.SourceSemanticFrame == transition.Source && item.ResultSemanticFrame == transition.Result)
                .ToListAsync();
            var supportedIndependent = evidence
                .Where(item => item.ContributionState == "Supported" && item.IsHumanVerifiedSupport)
                .Select(item => item.IndependentSourceIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var contradictions = evidence.Count(item => item.ContributionState == "Contradictory");
            _output.WriteLine($"TRANSITION {transition.Source} => {transition.Result} evidence={evidence.Count} independent={supportedIndependent} contradictions={contradictions}");
            Assert.True(supportedIndependent >= 3, "Expected at least three independent Founder supports for the transition.");
            Assert.Equal(0, contradictions);
        }

        _output.WriteLine("=== BATCH 001 ACCEPTANCE ===");
        _output.WriteLine("ACCEPTED_AND_PROCESSED: true");
        _output.WriteLine("FAMILIES: 12/12");
        _output.WriteLine("EXAMPLES: 120/120");
        _output.WriteLine("MEANING_NODES: 360");
        _output.WriteLine("MEANING_RELATION_EVIDENCE: 240");
        _output.WriteLine("TRANSITION_GROUPS_WITH_3PLUS_INDEPENDENT_SUPPORT: 4/4");
        _output.WriteLine("CONTRADICTORY_TRANSITION_EVIDENCE: 0");
    }

    private static async Task<LegendCurriculumManifestWorkItem?> FindWorkAsync(
        MasterAppDbContext db,
        string founderOid,
        DateTime submittedAt)
    {
        return await db.Set<LegendCurriculumManifestWorkItem>().AsNoTracking()
            .Where(item => item.FounderUserId == founderOid && item.FamilyCount == 12 && item.ExampleCount == 120 && item.CreatedUtc >= submittedAt.AddMinutes(-1))
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefaultAsync();
    }

    private static LegendConnectCurriculumBatchSubmission BuildFamily(FamilySpec spec)
    {
        var examples = spec.Examples.Select((example, index) =>
        {
            var function = example.IsResult ? spec.ResultFunction : spec.SourceFunction;
            var intent = example.IsResult ? spec.ResultIntent : spec.SourceIntent;
            var suffix = example.IsResult ? "result" : "source";
            var variations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conversation_function"] = function,
                ["intent"] = intent,
                ["domain_context"] = spec.Domain
            };
            var graph = new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("function", "conversation_function", function, example.FunctionSpan),
                    new LegendConnectMeaningNodeSubmission("intent", "intent", intent, example.IntentSpan),
                    new LegendConnectMeaningNodeSubmission("context", "domain_context", spec.Domain, spec.Domain.Replace('_', ' '))
                ],
                [
                    new LegendConnectMeaningRelationSubmission("function", "governs", "intent"),
                    new LegendConnectMeaningRelationSubmission("function", "applies-to", "context")
                ]);
            return new LegendConnectCurriculumExampleSubmission(
                example.Text,
                variations,
                graph,
                $"legend-120x120-b001-{spec.Key.Split('.').Last()}-{suffix}-{index + 1:00}");
        }).ToArray();

        var transition = new LegendConnectSemanticTransitionSubmission(
            new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
            {
                ["conversation_function"] = spec.SourceFunction,
                ["intent"] = spec.SourceIntent,
                ["domain_context"] = "$domain_context"
            }),
            new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
            {
                ["conversation_function"] = spec.ResultFunction,
                ["intent"] = spec.ResultIntent,
                ["domain_context"] = "$domain_context"
            }));

        return new LegendConnectCurriculumBatchSubmission(
            spec.Key,
            spec.Category,
            examples,
            [transition]);
    }

    private static string CanonicalFrame(string function, string intent) =>
        $"{{\"conversation_function\":\"{function}\",\"domain_context\":\"$domain_context\",\"intent\":\"{intent}\"}}";

    private static IReadOnlyList<FamilySpec> Batch001Specs() =>
    [
        RequestFamily("dm_request", "data_migration",
        [
            S("Before acting on the data migration, map the required outcome, fixed constraints, dependencies, and completion evidence.", "map", "required outcome, fixed constraints, dependencies, and completion evidence"),
            S("Figure out what the data migration must achieve, what cannot change, what it depends on, and what proves it is done.", "Figure out", "must achieve, what cannot change, what it depends on, and what proves it is done"),
            S("Separate the data migration goal from its constraints, prerequisites, and acceptance evidence before choosing a solution.", "Separate", "goal from its constraints, prerequisites, and acceptance evidence"),
            S("What must the data migration deliver, preserve, depend on, and satisfy before work begins?", "What must", "deliver, preserve, depend on, and satisfy"),
            S("Break the data migration request into outcome, non-negotiables, required inputs, and a clear finish line.", "Break", "outcome, non-negotiables, required inputs, and a clear finish line"),
            S("Clarify the data migration objective, limits, dependencies, and definition of done before planning.", "Clarify", "objective, limits, dependencies, and definition of done"),
            R("For the data migration, I will treat the intended outcome, hard limits, prerequisites, and acceptance evidence as separate parts of the request.", "treat", "intended outcome, hard limits, prerequisites, and acceptance evidence"),
            R("The data migration is framed by what success requires, what must remain fixed, what it depends on, and how completion will be verified.", "framed", "what success requires, what must remain fixed, what it depends on, and how completion will be verified"),
            R("I will organize the data migration around its objective, governing constraints, required inputs, and completion test.", "organize", "objective, governing constraints, required inputs, and completion test"),
            R("The data migration request becomes actionable when its outcome, boundaries, dependencies, and finish criteria are explicit.", "actionable", "outcome, boundaries, dependencies, and finish criteria")
        ]),
        RequestFamily("tc_request", "technology_choice",
        [
            S("Before selecting a technology choice, identify the outcome it must enable, the limits it must respect, its dependencies, and the acceptance test.", "identify", "outcome it must enable, the limits it must respect, its dependencies, and the acceptance test"),
            S("Untangle the technology choice request into the goal, non-negotiable constraints, required inputs, and proof of success.", "Untangle", "goal, non-negotiable constraints, required inputs, and proof of success"),
            S("What is the technology choice supposed to accomplish, preserve, rely on, and prove before we compare options?", "What is", "supposed to accomplish, preserve, rely on, and prove"),
            S("Define the technology choice objective, boundaries, prerequisites, and completion criteria before recommending anything.", "Define", "objective, boundaries, prerequisites, and completion criteria"),
            S("Interpret the technology choice request by separating desired outcome, hard constraints, dependencies, and decision evidence.", "Interpret", "desired outcome, hard constraints, dependencies, and decision evidence"),
            S("First isolate what success means for the technology choice, what cannot be violated, what must already be true, and how success will be recognized.", "isolate", "what success means, what cannot be violated, what must already be true, and how success will be recognized"),
            R("For the technology choice, the objective, non-negotiables, dependencies, and acceptance evidence form distinct parts of the request.", "form", "objective, non-negotiables, dependencies, and acceptance evidence"),
            R("I will frame the technology choice around the intended outcome, governing limits, prerequisites, and proof required for success.", "frame", "intended outcome, governing limits, prerequisites, and proof required for success"),
            R("The technology choice becomes clear when its target outcome, boundaries, dependencies, and decision test are stated separately.", "clear", "target outcome, boundaries, dependencies, and decision test"),
            R("I will keep the technology choice goal, constraints, required conditions, and success criteria distinct before evaluating solutions.", "keep", "goal, constraints, required conditions, and success criteria")
        ]),
        RequestFamily("ir_request", "incident_report",
        [
            S("Before responding to the incident report, determine the intended resolution, operational constraints, required evidence, and completion condition.", "determine", "intended resolution, operational constraints, required evidence, and completion condition"),
            S("Parse the incident report request into what must be achieved, what must be preserved, what information is required, and what closes the work.", "Parse", "what must be achieved, what must be preserved, what information is required, and what closes the work"),
            S("What outcome does the incident report require, which limits govern it, what does it depend on, and what counts as resolved?", "What outcome", "require, which limits govern it, what does it depend on, and what counts as resolved"),
            S("Separate the incident report objective from its constraints, evidence dependencies, and resolution test before taking action.", "Separate", "objective from its constraints, evidence dependencies, and resolution test"),
            S("Make the incident report actionable by identifying its goal, fixed boundaries, needed inputs, and closure criteria.", "identifying", "goal, fixed boundaries, needed inputs, and closure criteria"),
            S("Establish what the incident report is asking us to accomplish, avoid changing, obtain first, and verify at the end.", "Establish", "accomplish, avoid changing, obtain first, and verify at the end"),
            R("The incident report is organized around the required outcome, governing constraints, evidence dependencies, and closure test.", "organized", "required outcome, governing constraints, evidence dependencies, and closure test"),
            R("I will treat the incident report goal, non-negotiables, required evidence, and resolution criteria as separate requirements.", "treat", "goal, non-negotiables, required evidence, and resolution criteria"),
            R("For the incident report, success, constraints, prerequisites, and the definition of resolved are now explicit.", "explicit", "success, constraints, prerequisites, and the definition of resolved"),
            R("The incident report request becomes actionable once its outcome, boundaries, dependencies, and verification condition are distinguished.", "actionable", "outcome, boundaries, dependencies, and verification condition")
        ]),

        DiagnosticFamily("pl_diag", "product_launch",
        [
            S("Investigate the product launch problem by comparing plausible causes with supporting and conflicting evidence.", "Investigate", "plausible causes with supporting and conflicting evidence"),
            S("Which explanation best accounts for the product launch issue, and what evidence separates it from the alternatives?", "Which explanation", "accounts for the product launch issue, and what evidence separates it from the alternatives"),
            S("For the product launch, identify competing causes before treating any explanation as established.", "identify", "competing causes before treating any explanation as established"),
            S("Diagnose the product launch failure from the evidence rather than jumping to the first plausible cause.", "Diagnose", "from the evidence rather than jumping to the first plausible cause"),
            S("Use the product launch observations to distinguish likely causes from merely possible ones.", "distinguish", "likely causes from merely possible ones"),
            S("What product launch hypothesis has the strongest support, what conflicts with it, and what test would separate the leading candidates?", "What", "hypothesis has the strongest support, what conflicts with it, and what test would separate the leading candidates"),
            R("For the product launch, I will rank plausible causes by supporting and conflicting evidence, then use a discriminating test.", "rank", "plausible causes by supporting and conflicting evidence, then use a discriminating test"),
            R("The product launch diagnosis will keep the leading cause provisional until a test separates it from credible alternatives.", "diagnosis", "leading cause provisional until a test separates it from credible alternatives"),
            R("I will compare product launch hypotheses against the same evidence and choose the next test for maximum discrimination.", "compare", "hypotheses against the same evidence and choose the next test for maximum discrimination"),
            R("A product launch explanation becomes stronger only when evidence favors it over competing causes and the proposed test confirms the distinction.", "stronger", "evidence favors it over competing causes and the proposed test confirms the distinction")
        ]),
        DiagnosticFamily("dm_diag", "data_migration",
        [
            S("Diagnose the data migration problem by listing credible causes and testing each against the observed evidence.", "Diagnose", "credible causes and testing each against the observed evidence"),
            S("What could explain the data migration failure, and which observation would best distinguish the strongest possibilities?", "What could explain", "which observation would best distinguish the strongest possibilities"),
            S("Do not assume the data migration cause; compare competing explanations with what the evidence supports and contradicts.", "compare", "competing explanations with what the evidence supports and contradicts"),
            S("For the data migration, find the cause that best fits the evidence while preserving viable alternatives until tested.", "find", "cause that best fits the evidence while preserving viable alternatives until tested"),
            S("Which data migration hypotheses survive the available evidence, and what test would eliminate the most uncertainty?", "Which", "hypotheses survive the available evidence, and what test would eliminate the most uncertainty"),
            S("Evaluate possible data migration causes by evidence strength before deciding what to investigate next.", "Evaluate", "possible data migration causes by evidence strength before deciding what to investigate next"),
            R("For the data migration, I will rank plausible causes, note supporting and conflicting evidence, and select a discriminating test.", "rank", "plausible causes, note supporting and conflicting evidence, and select a discriminating test"),
            R("The data migration explanation remains a hypothesis until the selected test distinguishes it from other evidence-compatible causes.", "remains", "hypothesis until the selected test distinguishes it from other evidence-compatible causes"),
            R("I will compare data migration causes on common evidence and prioritize the test that most reduces uncertainty.", "compare", "causes on common evidence and prioritize the test that most reduces uncertainty"),
            R("A defensible data migration diagnosis ranks alternatives before testing the distinction that matters most.", "diagnosis", "ranks alternatives before testing the distinction that matters most")
        ]),
        DiagnosticFamily("tc_diag", "technology_choice",
        [
            S("Diagnose why the technology choice is underperforming by comparing credible explanations against the observed evidence.", "Diagnose", "credible explanations against the observed evidence"),
            S("Which cause best explains the technology choice problem, and what evidence would separate it from the next-best explanation?", "Which cause", "best explains the technology choice problem, and what evidence would separate it from the next-best explanation"),
            S("Investigate the technology choice issue without assuming a cause; rank alternatives by evidence and identify a decisive test.", "Investigate", "rank alternatives by evidence and identify a decisive test"),
            S("For the technology choice problem, compare hypotheses using both supporting and conflicting observations.", "compare", "hypotheses using both supporting and conflicting observations"),
            S("What competing explanations remain viable for the technology choice issue, and which test would discriminate among them?", "What", "competing explanations remain viable, and which test would discriminate among them"),
            S("Use evidence to narrow the technology choice causes before committing to a remediation.", "narrow", "causes before committing to a remediation"),
            R("For the technology choice, I will rank plausible causes by evidence and choose a test that separates the leading explanations.", "rank", "plausible causes by evidence and choose a test that separates the leading explanations"),
            R("The technology choice diagnosis will remain provisional until the discriminating evidence is observed.", "diagnosis", "remain provisional until the discriminating evidence is observed"),
            R("I will preserve competing technology choice hypotheses when the evidence does not yet distinguish them.", "preserve", "competing technology choice hypotheses when the evidence does not yet distinguish them"),
            R("A technology choice cause earns priority when it explains more evidence and survives a test designed against credible alternatives.", "priority", "explains more evidence and survives a test designed against credible alternatives")
        ]),

        DecisionFamily("rp_decision", "research_project",
        [
            S("Choose the research project approach by applying the hard constraints first, then comparing tradeoffs among the feasible options.", "Choose", "hard constraints first, then comparing tradeoffs among the feasible options"),
            S("Which research project option best satisfies the priorities without violating the non-negotiables?", "Which", "best satisfies the priorities without violating the non-negotiables"),
            S("Compare the research project alternatives against the same criteria and explain what each viable option gives up.", "Compare", "alternatives against the same criteria and explain what each viable option gives up"),
            S("For the research project, eliminate choices that fail mandatory constraints before weighing benefits and downsides.", "eliminate", "choices that fail mandatory constraints before weighing benefits and downsides"),
            S("What research project choice is best supported after constraints, priorities, risk, and tradeoffs are considered together?", "What", "choice is best supported after constraints, priorities, risk, and tradeoffs are considered together"),
            S("Evaluate the research project options consistently rather than selecting the one that is merely preferred.", "Evaluate", "options consistently rather than selecting the one that is merely preferred"),
            R("For the research project, I will compare feasible options against common criteria, state tradeoffs, and recommend the best-supported fit.", "compare", "feasible options against common criteria, state tradeoffs, and recommend the best-supported fit"),
            R("The research project recommendation will identify which criteria drive the choice, which disadvantages are accepted, and what could reverse it.", "recommendation", "which criteria drive the choice, which disadvantages are accepted, and what could reverse it"),
            R("I will reject research project options that violate hard constraints before weighing the remaining tradeoffs.", "reject", "options that violate hard constraints before weighing the remaining tradeoffs"),
            R("A defensible research project choice uses the same priorities for every viable option and makes the accepted downside explicit.", "choice", "same priorities for every viable option and makes the accepted downside explicit")
        ]),
        DecisionFamily("sf_decision", "system_failure",
        [
            S("Select the system failure response by ruling out options that violate hard requirements, then compare the remaining tradeoffs.", "Select", "ruling out options that violate hard requirements, then compare the remaining tradeoffs"),
            S("Which system failure remedy best satisfies the stated priorities and constraints?", "Which", "remedy best satisfies the stated priorities and constraints"),
            S("Compare system failure response options using common criteria, including downside risk and reversibility.", "Compare", "response options using common criteria, including downside risk and reversibility"),
            S("For the system failure, apply non-negotiable constraints before judging the benefits of each feasible response.", "apply", "non-negotiable constraints before judging the benefits of each feasible response"),
            S("What system failure action wins when every option is evaluated against the same requirements and tradeoffs?", "What", "action wins when every option is evaluated against the same requirements and tradeoffs"),
            S("Decide among system failure remedies by evidence and criteria, not by familiarity with an option.", "Decide", "remedies by evidence and criteria, not by familiarity with an option"),
            R("For the system failure, I will compare feasible remedies against the same criteria and make the tradeoffs explicit.", "compare", "feasible remedies against the same criteria and make the tradeoffs explicit"),
            R("The system failure recommendation will state the winning criteria, accepted downside, and conditions that would favor another remedy.", "recommendation", "winning criteria, accepted downside, and conditions that would favor another remedy"),
            R("I will exclude system failure responses that violate mandatory constraints before ranking the remaining options.", "exclude", "responses that violate mandatory constraints before ranking the remaining options"),
            R("A system failure decision is strongest when alternatives face the same criteria and reversal conditions are visible.", "decision", "alternatives face the same criteria and reversal conditions are visible")
        ]),
        DecisionFamily("pl_decision", "product_launch",
        [
            S("Choose the product launch path by applying mandatory constraints first and comparing tradeoffs among the viable alternatives.", "Choose", "mandatory constraints first and comparing tradeoffs among the viable alternatives"),
            S("Which product launch option best fits the priorities while respecting every hard requirement?", "Which", "best fits the priorities while respecting every hard requirement"),
            S("Compare product launch alternatives with the same decision criteria instead of relying on preference.", "Compare", "alternatives with the same decision criteria instead of relying on preference"),
            S("For the product launch, eliminate paths that fail non-negotiables and then weigh benefits, risks, and reversibility.", "eliminate", "paths that fail non-negotiables and then weigh benefits, risks, and reversibility"),
            S("What product launch approach has the best-supported fit after constraints and tradeoffs are evaluated consistently?", "What", "approach has the best-supported fit after constraints and tradeoffs are evaluated consistently"),
            S("Evaluate product launch options by criteria that can be explained and challenged, not by intuition alone.", "Evaluate", "options by criteria that can be explained and challenged, not by intuition alone"),
            R("For the product launch, I will compare feasible paths against common criteria, state tradeoffs, and recommend the strongest fit.", "compare", "feasible paths against common criteria, state tradeoffs, and recommend the strongest fit"),
            R("The product launch recommendation will expose the decisive criteria, accepted disadvantages, and reversal conditions.", "recommendation", "decisive criteria, accepted disadvantages, and reversal conditions"),
            R("I will remove product launch options that violate hard requirements before ranking the alternatives that remain.", "remove", "options that violate hard requirements before ranking the alternatives that remain"),
            R("A product launch decision is defensible when every feasible option is judged by the same priorities and tradeoffs.", "decision", "every feasible option is judged by the same priorities and tradeoffs")
        ]),

        EvidenceFamily("tc_evidence", "technology_choice",
        [
            S("Before drawing a technology choice conclusion, classify what is confirmed, inferred, estimated, assumed, and still unknown.", "classify", "confirmed, inferred, estimated, assumed, and still unknown"),
            S("What do we actually know about the technology choice, what are we inferring, and what remains uncertain?", "What", "actually know, what are we inferring, and what remains uncertain"),
            S("Separate technology choice facts from supported deductions, estimates, assumptions, and missing evidence.", "Separate", "facts from supported deductions, estimates, assumptions, and missing evidence"),
            S("For the technology choice, label the evidence by epistemic status before using it to justify a decision.", "label", "evidence by epistemic status before using it to justify a decision"),
            S("Identify which technology choice claims are observed, which are inferred, which are estimates, and which are unresolved.", "Identify", "claims are observed, which are inferred, which are estimates, and which are unresolved"),
            S("Do not treat every technology choice statement as a fact; classify the strength and status of each claim first.", "classify", "strength and status of each claim first"),
            R("For the technology choice, I will keep confirmed facts, supported inferences, estimates, assumptions, and unknowns distinct.", "keep", "confirmed facts, supported inferences, estimates, assumptions, and unknowns distinct"),
            R("Any technology choice conclusion will be calibrated to the quality and completeness of the evidence supporting it.", "calibrated", "quality and completeness of the evidence supporting it"),
            R("I will state which technology choice claims are established, which are reasoned from evidence, and which remain unresolved.", "state", "claims are established, which are reasoned from evidence, and which remain unresolved"),
            R("The technology choice analysis will not promote an assumption or estimate into a fact merely because it supports the preferred option.", "analysis", "assumption or estimate into a fact merely because it supports the preferred option")
        ]),
        EvidenceFamily("dm_evidence", "data_migration",
        [
            S("Classify the data migration evidence into confirmed facts, supported inferences, estimates, assumptions, and unknowns before concluding.", "Classify", "confirmed facts, supported inferences, estimates, assumptions, and unknowns"),
            S("Which data migration claims are directly established, which are inferred, and which still need evidence?", "Which", "claims are directly established, which are inferred, and which still need evidence"),
            S("Separate what is known about the data migration from what is estimated, assumed, or unresolved.", "Separate", "what is known, what is estimated, assumed, or unresolved"),
            S("For the data migration, mark the epistemic status of each important claim before using it in the plan.", "mark", "epistemic status of each important claim"),
            S("Distinguish observed data migration facts from evidence-supported deductions and unsupported assumptions.", "Distinguish", "observed data migration facts from evidence-supported deductions and unsupported assumptions"),
            S("What remains unknown about the data migration after confirmed observations and reasonable inferences are separated?", "What remains", "unknown after confirmed observations and reasonable inferences are separated"),
            R("For the data migration, I will label facts, supported inferences, estimates, assumptions, and unknowns separately.", "label", "facts, supported inferences, estimates, assumptions, and unknowns separately"),
            R("The data migration conclusion will be no stronger than the evidence that supports it.", "conclusion", "no stronger than the evidence that supports it"),
            R("I will keep unresolved data migration claims visible instead of silently treating them as established.", "keep", "unresolved data migration claims visible instead of silently treating them as established"),
            R("A data migration estimate or assumption remains qualified until independent evidence changes its status.", "qualified", "estimate or assumption remains qualified until independent evidence changes its status")
        ]),
        EvidenceFamily("sr_evidence", "strategic_risk",
        [
            S("Before acting on strategic risk, distinguish confirmed evidence from inference, estimates, assumptions, and unknowns.", "distinguish", "confirmed evidence from inference, estimates, assumptions, and unknowns"),
            S("What is actually established about the strategic risk, what follows from evidence, and what is still uncertain?", "What", "actually established, what follows from evidence, and what is still uncertain"),
            S("Classify strategic risk claims by epistemic status before using them to justify a response.", "Classify", "claims by epistemic status before using them to justify a response"),
            S("Separate strategic risk facts, evidence-backed deductions, estimates, assumptions, and unresolved questions.", "Separate", "facts, evidence-backed deductions, estimates, assumptions, and unresolved questions"),
            S("Which strategic risk statements are observations, which are reasoned conclusions, and which still depend on assumptions?", "Which", "statements are observations, which are reasoned conclusions, and which still depend on assumptions"),
            S("Do not collapse uncertainty around strategic risk; identify what is known and what evidence is missing.", "identify", "what is known and what evidence is missing"),
            R("For strategic risk, I will keep facts, supported inferences, estimates, assumptions, and unknowns explicitly separated.", "keep", "facts, supported inferences, estimates, assumptions, and unknowns explicitly separated"),
            R("A strategic risk conclusion will remain calibrated to both evidence strength and unresolved uncertainty.", "calibrated", "evidence strength and unresolved uncertainty"),
            R("I will preserve unknowns in the strategic risk analysis rather than filling them with unsupported confidence.", "preserve", "unknowns rather than filling them with unsupported confidence"),
            R("Strategic risk estimates and assumptions stay qualified until stronger evidence justifies changing their status.", "qualified", "estimates and assumptions stay qualified until stronger evidence justifies changing their status")
        ])
    ];

    private static FamilySpec RequestFamily(string suffix, string domain, IReadOnlyList<ExampleSpec> examples) =>
        new($"legend.maxintent.b001.request.{suffix}", "Natural request interpretation across structurally diverse language",
            domain, "complex_request", "identify_goal_constraints", "request_interpretation", "state_goal_constraints", examples);

    private static FamilySpec DiagnosticFamily(string suffix, string domain, IReadOnlyList<ExampleSpec> examples) =>
        new($"legend.maxintent.b001.diagnostic.{suffix}", "Evidence-grounded causal diagnosis across structurally diverse language",
            domain, "diagnostic_request", "identify_cause_from_evidence", "diagnostic_response", "rank_and_test_causes", examples);

    private static FamilySpec DecisionFamily(string suffix, string domain, IReadOnlyList<ExampleSpec> examples) =>
        new($"legend.maxintent.b001.decision.{suffix}", "Constraint-aware decision reasoning across structurally diverse language",
            domain, "decision_request", "compare_options_constraints", "decision_response", "recommend_with_tradeoffs", examples);

    private static FamilySpec EvidenceFamily(string suffix, string domain, IReadOnlyList<ExampleSpec> examples) =>
        new($"legend.maxintent.b001.evidence.{suffix}", "Epistemic evidence calibration across structurally diverse language",
            domain, "evidence_question", "classify_epistemic_status", "evidence_calibrated_response", "label_fact_inference_unknown", examples);

    private static ExampleSpec S(string text, string functionSpan, string intentSpan) => new(text, functionSpan, intentSpan, false);
    private static ExampleSpec R(string text, string functionSpan, string intentSpan) => new(text, functionSpan, intentSpan, true);
}
