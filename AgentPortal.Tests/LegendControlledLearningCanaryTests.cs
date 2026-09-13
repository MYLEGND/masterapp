using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Claims;
using AgentPortal.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    // Invoked only by the existing explicit resource canary. No synthetic
    // completion, evaluator score, promotion row, or model answer is seeded.
    private async Task VerifyControlledLearningLifecycleAsync()
    {
        const string root = "/Users/zacowen/LEGEND-models/artifacts/learning-canary";
        var phase = Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_LEARNING_PHASE");
        Assert.True(phase is "baseline" or "train" or "evaluate", "Explicit baseline, train or evaluate phase is required.");
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddControlledFoundation().Build();
        Assert.Equal("FounderMac", configuration["LegendConnect:Foundation:HostKind"]);
        Assert.True(LegendConnectModelInferenceTransport.IsControlledFoundationHost(configuration));
        var founder = LegendConnectModelInferenceTransport.ResolveConfiguredFounderObjectId(configuration)!;
        // Explicit isolated authenticated actor fixture, never a claim that a
        // live production Founder session or production database was tested.
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founder)], "controlled-learning-fixture"));
        FounderGuard.EnsureFounderOrThrow(actor);
        var codeSha = Environment.GetEnvironmentVariable("LEGEND_VALIDATION_CANDIDATE_SHA");
        Assert.True(codeSha is { Length: 40 } && codeSha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "An exact candidate SHA is required.");
        configuration["LegendConnect:ModelTraining:Backend"] = "ControlledMlx";
        configuration["LegendConnect:ModelTraining:BaseModel"] = configuration["LegendConnect:Foundation:Model"];
        configuration["LegendConnect:ModelTraining:Enabled"] = "true";
        configuration["LegendConnect:ModelEvaluation:Enabled"] = "true";
        configuration["LegendConnect:ModelEvaluation:PromptSetVersion"] = "bounded-boolean-learning-v1";
        configuration["LegendConnect:ModelEvaluation:CodeSha"] = codeSha;
        configuration["LegendConnect:ModelPromotion:Enabled"] = "true";
        Assert.False(string.IsNullOrWhiteSpace(configuration["LegendConnect:ModelTraining:TrainerCodeSha256"]), "A frozen trainer SHA is required.");
        Directory.CreateDirectory(root);
        Assert.False(new DirectoryInfo(root).LinkTarget is not null, "A symbolic-link artifact directory is forbidden.");
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "isolated-canonical.db")).Options);
        await db.Database.EnsureCreatedAsync();
        Assert.All(await db.Set<LegendCurriculumFamily>().ToListAsync(), family => Assert.StartsWith("learning-canary-", family.FamilyKey));
        if (!await db.Set<LegendConnectRuntimePolicy>().AnyAsync())
        {
            // Empty isolated fixture: no historical corpus exists to replay.
            ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
            db.Add(new LegendConnectRuntimePolicy { ScopeKey = "Global", LearningEnabled = true,
                LanguageIntelligenceReevaluationPhase = "Complete",
                CompletedLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                TargetLanguageIntelligenceEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current });
            await db.SaveChangesAsync();
        }
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var declarations = LearningDeclarations();
        foreach (var declaration in declarations)
        {
            var admitted = await curriculum.SubmitFounderBatchAsync(declaration.Batch, sourceLanguageCode: declaration.Language);
            Assert.True(admitted.Succeeded, admitted.ErrorCode + ": " + admitted.Message);
        }
        var rows = await (from target in db.Set<LegendCurriculumExample>()
            join source in db.Set<LegendCurriculumExample>() on target.DerivedFromCurriculumExampleId equals source.Id
            join sourceUnit in db.Set<LegendLanguageTextUnit>() on source.TextUnitId equals sourceUnit.Id
            join targetUnit in db.Set<LegendLanguageTextUnit>() on target.TextUnitId equals targetUnit.Id
            select new { Source = source, Target = target, SourceUnit = sourceUnit, TargetUnit = targetUnit }).ToListAsync();
        var rightsIndex = 0;
        foreach (var row in rows)
        {
            Assert.Equal("FounderApproved", row.SourceUnit.Provenance);
            Assert.Equal("SystemValidatedMachine", row.TargetUnit.Provenance);
            Assert.True(LegendFoundationConversationControl.TryValidateOracle(row.SourceUnit.Text, row.TargetUnit.Text, row.TargetUnit.LanguageCode));
            var evidence = LegendConnectTrainingDatasetCompiler.CurriculumEvidenceIdentity(row.Source.Id, row.Target.Id,
                row.SourceUnit.LanguageCode + ":" + row.TargetUnit.LanguageCode,
                row.SourceUnit.NormalizedHash, row.TargetUnit.NormalizedHash, "SystemValidatedMachine");
            var prefix = "LegendConnect:ModelTraining:RightsAttestations:" + rightsIndex++ + ":";
            configuration[prefix + "EvidenceIdentity"] = evidence;
            configuration[prefix + "SourceTextHash"] = LearningHash(row.SourceUnit.Text);
            configuration[prefix + "TargetTextHash"] = LearningHash(row.TargetUnit.Text);
            configuration[prefix + "RightsBasis"] = "Owned";
            configuration[prefix + "SourceReference"] = "first-party-executable-mathematical-oracle:v1; finite synthetic variables and independently computed targets; no hosted teacher or private corpus";
            configuration[prefix + "PrivacyScope"] = "PublicNonPersonal";
            configuration[prefix + "TrainingPurpose"] = "TransferableSkill";
            configuration[prefix + "SharedTrainingPermitted"] = "true";
        }
        var compiler = new LegendConnectTrainingDatasetCompiler(db, configuration);
        var manifest = await compiler.CompileAsync();
        Assert.NotEmpty(manifest.Training);
        Assert.NotEmpty(manifest.HeldOut);
        foreach (var category in LegendFoundationConversationControl.RequiredCategories)
            Assert.True(manifest.HeldOut.Where(example =>
                LegendFoundationConversationControl.TryRead(example.SourceText, out var actual, out _, out _) && actual == category)
                .Select(example => example.SplitGroupIdentity).Distinct().Count() >= 2, "Insufficient frozen controls: " + category);
        Assert.Contains(manifest.HeldOut, example => example.TargetLanguageCode == "ht");
        var frozen = JsonSerializer.Serialize(manifest);
        var frozenPath = Path.Combine(root, "frozen-manifest.json");
        if (File.Exists(frozenPath)) Assert.Equal(await File.ReadAllTextAsync(frozenPath), frozen);
        else
        {
            Assert.Equal("baseline", phase);
            await File.WriteAllTextAsync(frozenPath, frozen);
        }
        using var clients = new LearningControlledClients();
        var transport = new LearningTraceTransport(
            new LegendConnectModelInferenceTransport(clients, configuration, NullLogger<LegendConnectModelInferenceTransport>.Instance),
            Path.Combine(root, phase + "-inference.jsonl"));
        var backend = new ControlledLegendConnectModelTrainingBackend(configuration, clients);
        var serving = new LegendConnectActiveModelInference(db, transport, configuration, backend);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var baselinePath = Path.Combine(root, phase == "evaluate" ? "baseline-" + codeSha + ".json" : "baseline.json");
        if (phase is "baseline" or "evaluate")
        {
            if (phase == "baseline")
                Assert.Empty(await db.Set<LegendConnectModelTrainingRun>().ToListAsync());
            else
            {
                var preserved = await db.Set<LegendConnectModelTrainingRun>().SingleAsync();
                Assert.Equal("TrainingCompleted", preserved.State);
                Assert.Equal("Rejected", preserved.EvaluationState);
                Assert.Equal("model_evaluation_incomplete_case", preserved.FailureCode);
                Assert.False(preserved.PromotionState == "Promoted");
            }
            Assert.False(File.Exists(baselinePath), "The frozen baseline is immutable; use its recorded result.");
            var baseline = new List<object>();
            foreach (var example in manifest.HeldOut)
            {
                var clock = Stopwatch.StartNew();
                var task = example.ToTaskRequest() with { RequestingActorId = founder };
                var reply = await transport.GenerateAsync(configuration["LegendConnect:Foundation:Model"]!, task, deadline.Token);
                Assert.True(reply.Succeeded, reply.ErrorCode);
                baseline.Add(new { example.EvidenceIdentity, reply.Text, reply.ModelVersion, reply.InferenceSettings,
                    booleanProblem = IsBooleanLearningProblem(example), inputIdentity = LearningInputIdentity(task),
                    correct = LegendFoundationConversationControl.MatchesVerifiedTarget(example.SourceText, example.TargetText, reply.Text!),
                    elapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
            }
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(new { phase, executedTraining = false, syntheticIsolatedDatabase = true, manifest.DatasetIdentity, codeSha, cases = baseline }));
            if (phase == "baseline") return; // Evaluation-only recaptures a current base without retraining.
        }
        Assert.True(File.Exists(baselinePath), "Actual frozen baseline inference is required before training.");
        Dictionary<string, JsonElement> frozenBaseline;
        using (var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath)))
        {
            Assert.Equal(manifest.DatasetIdentity, baseline.RootElement.GetProperty("DatasetIdentity").GetString());
            Assert.Equal(codeSha, baseline.RootElement.GetProperty("codeSha").GetString());
            frozenBaseline = baseline.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(
                item => item.GetProperty("EvidenceIdentity").GetString()!, item => item.Clone(), StringComparer.Ordinal);
            Assert.Contains(frozenBaseline.Values, item => item.GetProperty("booleanProblem").GetBoolean() && !item.GetProperty("correct").GetBoolean());
        }
        var training = new LegendConnectModelTrainingService(db, compiler, backend, configuration);
        LegendConnectModelTrainingRun? run = null;
        for (var iteration = 0; phase == "train" && iteration < 900; iteration++)
        {
            await training.ProcessOneAsync(deadline.Token);
            run = await db.Set<LegendConnectModelTrainingRun>().SingleAsync(deadline.Token);
            if (run.State is "TrainingCompleted" or "Failed") break;
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
        if (phase == "evaluate") run = await db.Set<LegendConnectModelTrainingRun>().SingleAsync(deadline.Token);
        Assert.NotNull(run);
        var evaluation = new LegendConnectModelEvaluationService(db, compiler, new LocalLegendConnectModelEvaluationBackend(), serving,
            configuration, transport, backend);
        if (phase == "evaluate")
            await evaluation.EvaluateManifestAsync(run, manifest, deadline.Token);
        else if (run.State == "TrainingCompleted") await evaluation.ProcessOneAsync(deadline.Token);
        var comparison = manifest.HeldOut.Select(example =>
        {
            var candidate = transport.Calls.FirstOrDefault(call => call.RequestedModel == run.ChallengerModelVersion && call.Task.Input == example.SourceText);
            var baseline = frozenBaseline[example.EvidenceIdentity];
            return new { example.EvidenceIdentity, booleanProblem = IsBooleanLearningProblem(example),
                baselineCorrect = baseline.GetProperty("correct").GetBoolean(),
                candidateCorrect = candidate is not null && candidate.Result.Succeeded && candidate.Result.Text is not null &&
                    LegendFoundationConversationControl.MatchesVerifiedTarget(example.SourceText, example.TargetText, candidate.Result.Text),
                candidateExecuted = candidate is not null,
                inputMatches = candidate is not null && LearningInputIdentity(candidate.Task) == baseline.GetProperty("inputIdentity").GetString(),
                settingsMatch = candidate?.Result.InferenceSettings is not null &&
                    LegendConnectServingEvaluationContracts.ComparableSettings(candidate.Result.InferenceSettings) ==
                    LegendConnectServingEvaluationContracts.ComparableSettings(baseline.GetProperty("InferenceSettings").GetString()!),
                candidateText = candidate?.Result.Text, baselineText = baseline.GetProperty("Text").GetString() };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, phase == "evaluate" ? "heldout-comparison-" + codeSha + ".json" : "heldout-comparison.json"), JsonSerializer.Serialize(comparison));
        // These acceptance checks cannot grant promotion; the existing service
        // still requires its independent scores and complete runtime proof.
        var demonstrated = comparison.All(item => item.candidateExecuted && item.inputMatches && item.settingsMatch &&
                (!item.baselineCorrect || item.candidateCorrect)) &&
            comparison.Count(item => item.booleanProblem && item.candidateCorrect) > comparison.Count(item => item.booleanProblem && item.baselineCorrect);
        var promotion = new LegendConnectModelPromotionService(db, compiler, configuration, backend);
        if (run.EvaluationState == "Passed" && demonstrated) await promotion.PromoteAsync(run.Id, deadline.Token);
        var selected = await serving.ResolveConversationModelAsync(deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(root, phase == "evaluate" ? "lifecycle-result-" + codeSha + ".json" : "lifecycle-result.json"), JsonSerializer.Serialize(new
        {
            manifest.DatasetIdentity, codeSha, run.Id, run.RunKey, run.State, run.TrainingProvider, run.ChallengerModelVersion,
            run.EvaluationState, run.PromotionState, run.HeldOutScore, run.RegressionScore, run.FailureCode, run.FailureDetail,
            selected, demonstratedBooleanImprovementWithoutRegression = demonstrated, clients.ControlledClientRequests, clients.BlockedExternalClientRequests, syntheticIsolatedDatabase = true, canonicalAdmission = "IndependentExecutableOracleValidation"
        }));
        Assert.True(demonstrated, "No verified held-out Boolean improvement with unchanged controls/settings was demonstrated; candidate is not accepted.");
        Assert.Equal("TrainingCompleted", run.State);
        Assert.Equal("Passed", run.EvaluationState);
        Assert.Equal("Promoted", run.PromotionState);
        Assert.Equal(run.Id, selected.ModelTrainingRunId);
        Assert.Equal(run.ChallengerModelVersion, selected.ModelVersion);
        var consumed = await transport.GenerateAsync(selected.ModelVersion!, manifest.HeldOut[0].ToTaskRequest() with
        { RequestingActorId = founder, AdapterVersion = selected.AdapterVersion }, deadline.Token);
        Assert.True(consumed.Succeeded, consumed.ErrorCode);
        Assert.Equal(selected.ModelVersion, consumed.ModelVersion);
        await File.WriteAllTextAsync(Path.Combine(root, "runtime-consumption.json"), JsonSerializer.Serialize(new
        { selected, consumed.ModelVersion, consumed.Text, consumed.InferenceSettings }));
    }

    private static bool IsBooleanLearningProblem(LegendConnectTrainingDatasetExample example)
    {
        using var source = JsonDocument.Parse(example.SourceText);
        return source.RootElement.GetProperty("oracle").GetProperty("kind").GetString() == "boolean_premises";
    }

    private static string LearningInputIdentity(LegendModelTaskRequest task) =>
        LearningHash(JsonSerializer.Serialize(task with { AdapterVersion = null }));

    private sealed record LearningInferenceCall(string RequestedModel, LegendModelTaskRequest Task,
        LegendModelEvaluationGenerationResult Result, double ElapsedMilliseconds);

    // Test evidence only. Every result is returned unchanged from the real
    // transport; this observer cannot produce answers or alter app authority.
    private sealed class LearningTraceTransport(ILegendConnectModelInferenceTransport actual, string path) : ILegendConnectModelInferenceTransport
    {
        public List<LearningInferenceCall> Calls { get; } = [];
        public async Task<LegendModelEvaluationGenerationResult> GenerateAsync(string model, LegendModelTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            var clock = Stopwatch.StartNew();
            var result = await actual.GenerateAsync(model, task, cancellationToken);
            var call = new LearningInferenceCall(model, task, result, clock.Elapsed.TotalMilliseconds);
            Calls.Add(call);
            var line = JsonSerializer.Serialize(call) + Environment.NewLine;
            if ((File.Exists(path) ? new FileInfo(path).Length : 0) + Encoding.UTF8.GetByteCount(line) > 8 * 1024 * 1024)
                throw new InvalidOperationException("The bounded isolated learning evidence artifact is full.");
            await File.AppendAllTextAsync(path, line, cancellationToken);
            return result;
        }
    }

    private sealed class LearningControlledClients : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        public int ControlledClientRequests { get; private set; }
        public int BlockedExternalClientRequests { get; private set; }
        public HttpClient CreateClient(string name)
        {
            if (name == "LegendLocalFoundation") { ControlledClientRequests++; return _client; }
            BlockedExternalClientRequests++;
            throw new InvalidOperationException("The mathematical learning canary forbids external clients: " + name);
        }
        public void Dispose() => _client.Dispose();
    }

    private static string LearningHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<(string Language, LegendConnectCurriculumBatchSubmission Batch)> LearningDeclarations()
    {
        var declarations = new List<(string, LegendConnectCurriculumBatchSubmission)>();
        var index = 0;
        void Pair(string language, LegendConnectCurriculumExampleSubmission first, LegendConnectCurriculumExampleSubmission second) =>
            declarations.Add((language, new("learning-canary-" + index++, "foundation.conversation", [first, second])));
        LegendConnectCurriculumExampleSubmission Control(string kind, int number)
        {
            var a = 100 + number; var b = 400 + number; var label = "M" + number;
            var category = kind switch { "exact_format" => "instruction_following", "updated_value" => "multiturn", "latest_record" => "grounding", "ht_sum" => "multilingual", _ => "reasoning" };
            var messages = new List<object>(); string target;
            switch (kind)
            {
                case "exact_format": messages.Add(new { role = "user", content = $"For case {label}, return exactly {label}:{a}:{b} with no explanation." }); target = $"{label}:{a}:{b}"; break;
                case "updated_value": messages.Add(new { role = "user", content = $"Record the value for {label} as {a}." }); messages.Add(new { role = "assistant", content = "Recorded." }); messages.Add(new { role = "user", content = $"Change {label} to {b}. Return only its current integer value." }); target = b.ToString(); break;
                case "latest_record": messages.Add(new { role = "user", content = $"For {label}, the supplied archived record says {a}; the supplied current approved record says {b}. Use only these records and return the current integer value." }); target = b.ToString(); break;
                case "ht_sum": messages.Add(new { role = "user", content = $"Ka {label}: kalkile {a} + {b}. Reponn ak chif la sèlman." }); target = (a + b).ToString(); break;
                default: messages.Add(new { role = "user", content = $"Case {label}: calculate {a} + {b}. Return only the integer." }); target = (a + b).ToString(); break;
            }
            return new(JsonSerializer.Serialize(new { schema = "legend-foundation-control-v1", category, scenario_identity = kind + number,
                messages, oracle = new { kind, a, b, label } }), new Dictionary<string, string> { ["case"] = label }, ExpectedResponse: target);
        }
        foreach (var kind in new[] { "exact_format", "updated_value", "latest_record", "ht_sum" })
            for (var number = 0; number < 32; number += 2)
                Pair(kind == "ht_sum" ? "ht" : "en", Control(kind, number), Control(kind, number + 1));
        var random = new Random(271828);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new (int Variables, int[][] Premises)[]
        {
            (1, [[1]]), (1, [[1], [-1]]), (1, [[1], [1], [-1]]), (1, [[1], [1], [-1], [-1]]),
            (2, [[-1, 2], [1], [-2]]), (3, [[-1, 2], [-2, 3], [1], [-3]])
        };
        var dispositions = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 4096 && identities.Count < 48; attempt++)
        {
            var variables = attempt < seeds.Length ? seeds[attempt].Variables : random.Next(1, 5);
            var premises = attempt < seeds.Length ? seeds[attempt].Premises : Enumerable.Range(0, random.Next(2, 7)).Select(_ => Enumerable.Range(1, variables)
                .OrderBy(_ => random.Next()).Take(random.Next(1, variables + 1)).Select(atom => random.Next(2) == 0 ? atom : -atom).ToArray()).ToArray();
            using var oracle = JsonDocument.Parse(JsonSerializer.Serialize(new { kind = "boolean_premises", variables, premises }));
            if (!LegendFoundationConversationControl.TryEvaluateBooleanOracle(oracle.RootElement, out var instruction, out var target, out var identity) || !identities.Add(identity)) continue;
            using var mathematicalLabel = JsonDocument.Parse(target);
            dispositions.Add(mathematicalLabel.RootElement.GetProperty("consistent").GetBoolean() ? "consistent" :
                mathematicalLabel.RootElement.GetProperty("possible_false_premises").GetArrayLength() == 1 ? "unique" : "underdetermined");
            var example = new LegendConnectCurriculumExampleSubmission(JsonSerializer.Serialize(new
            { schema = "legend-foundation-control-v1", category = "reasoning", scenario_identity = identity,
                messages = new[] { new { role = "user", content = instruction } }, oracle = oracle.RootElement }),
                new Dictionary<string, string> { ["case"] = identity[..12] }, ExpectedResponse: target);
            Pair("en", example, Control("sum", 1000 + identities.Count));
        }
        Assert.Equal(48, identities.Count);
        Assert.Equal(3, dispositions.Count);
        return declarations;
    }
}
