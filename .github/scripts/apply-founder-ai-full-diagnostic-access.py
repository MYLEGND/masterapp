from pathlib import Path


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit(f'anchor changed: {label}')
    return text.replace(old, new, 1)

service = Path('AgentPortal/Services/LegendFounderAiConversationService.cs')
text = service.read_text()

text = replace_once(text,
    '    private const int MaximumProviderConversationCharacters = 180_000;\n'
    '    private const int MinimumLatestMessageTailCharacters = 12_000;\n',
    '    private const int MaximumProviderConversationCharacters = 400_000;\n'
    '    private const int MinimumLatestMessageTailCharacters = 32_000;\n',
    'provider conversation capacity')
text = replace_once(text,
    '    private const int MaximumCasualOutputTokens = 1_200;\n',
    '    private const int MaximumCasualOutputTokens = 8_000;\n',
    'casual output ceiling')
text = replace_once(text,
    '    private const int MaximumReadOnlyToolSeconds = 20;\n'
    '    private const int MinimumToolOutputCharacters = 20_000;\n'
    '    private const int MaximumToolOutputCharacters = 80_000;\n',
    '    private const int MaximumReadOnlyToolSeconds = 60;\n'
    '    private const int MinimumToolOutputCharacters = 40_000;\n'
    '    private const int MaximumToolOutputCharacters = 180_000;\n',
    'tool budgets')
text = replace_once(text,
    '                    120,\n'
    '                45,\n'
    '                240);\n',
    '                    300,\n'
    '                45,\n'
    '                600);\n',
    'request timeout')
text = replace_once(text,
    '                    8_000,\n'
    '                1_500,\n'
    '                16_000);\n',
    '                    32_000,\n'
    '                1_500,\n'
    '                64_000);\n',
    'provider output tokens')

old = '''                    var toolOutput =
                        await ExecuteFounderToolWithBudgetAsync(
                            founder,
                            call,
                            mode,
                            request.FounderCommandConfirmed,
                            ResolveReadOnlyToolBudget(remaining),
                            toolOutputBudget,
                            effectiveToken);

                    if (IsReadOnlyFounderTool(call.Name))
                    {
                        governedInspectionCompleted = true;
                    }
'''
new = '''                    var toolOutput =
                        await ExecuteFounderToolWithBudgetAsync(
                            founder,
                            call,
                            mode,
                            request.FounderCommandConfirmed,
                            ResolveReadOnlyToolBudget(remaining),
                            toolOutputBudget,
                            effectiveToken);

                    // A governed inspection is complete only after a read tool
                    // returned usable governed evidence. Structured failures are
                    // fed back to OpenAI so it can continue with independent reads;
                    // one unavailable authority must not abort the whole diagnosis.
                    if (IsReadOnlyFounderTool(call.Name) &&
                        IsSuccessfulFounderToolOutput(toolOutput))
                    {
                        governedInspectionCompleted = true;
                    }
'''
text = replace_once(text, old, new, 'successful governed read invariant')

old = '''        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Legend Founder AI read-only tool {Tool} exceeded its {Seconds:F1}-second dynamic budget; returning a structured diagnostic.",
                call.Name,
                readOnlyBudget.TotalSeconds);

            throw new LegendFounderAiToolExecutionException(
                call.Name,
                "tool_timeout",
                "timeout");
        }
'''
new = '''        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Legend Founder AI read-only tool {Tool} exceeded its {Seconds:F1}-second dynamic budget; returning a structured diagnostic.",
                call.Name,
                readOnlyBudget.TotalSeconds);

            return SerializeFounderToolFailure(
                call.Name,
                "tool_timeout",
                "timeout",
                $"The governed read exceeded its {readOnlyBudget.TotalSeconds:F1}-second budget. Independent reads may continue.");
        }
'''
text = replace_once(text, old, new, 'read timeout structured continuation')

old = '''        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI read-only tool {Tool} failed before a response could be produced.",
                call.Name);

            throw new LegendFounderAiToolExecutionException(
                call.Name,
                "tool_read_failed",
                "governed_tool");
        }
    }

    private static bool IsReadOnlyFounderTool(
'''
new = '''        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND Founder AI read-only tool {Tool} failed before a response could be produced.",
                call.Name);

            return SerializeFounderToolFailure(
                call.Name,
                "tool_read_failed",
                exception.GetType().Name,
                "This governed read failed. The exact tool identity is preserved and independent read authorities may continue.");
        }
    }

    private static string SerializeFounderToolFailure(
        string tool,
        string reason,
        string failureKind,
        string detail) =>
        JsonSerializer.Serialize(
            new
            {
                succeeded = false,
                tool,
                reason,
                failureKind,
                detail
            },
            JsonOptions);

    private static bool IsSuccessfulFounderToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind != JsonValueKind.Object ||
                   !document.RootElement.TryGetProperty("error", out _) &&
                   !(document.RootElement.TryGetProperty("succeeded", out var succeeded) &&
                     succeeded.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsReadOnlyFounderTool(
'''
text = replace_once(text, old, new, 'read failure continuation helpers')

old = '''            "canonical", "retained knowledge", "retained evidence",
            "governed inspection", "current authority",
            "curriculum", "train legend", "training status",
            "model readiness", "readiness", "alignment", "provenance",
            "evidence", "system state", "system status", "metrics", "metric",
            "provider capacity", "azure", "corpus", "production",
            "deployment", "repository", "github", "pull request",
            "branch", "commit", "workflow", "ci", "coverage"
'''
new = '''            "canonical", "retained knowledge", "retained evidence",
            "governed inspection", "current authority",
            "curriculum", "train legend", "training status", "training pipeline",
            "model readiness", "readiness", "alignment", "provenance",
            "evidence", "system state", "system status", "metrics", "metric",
            "provider capacity", "azure", "corpus", "production",
            "deployment", "repository", "github", "pull request",
            "branch", "commit", "workflow", "ci", "coverage",
            "architecture", "system prompt", "developer prompt", "prompt template",
            "tool registry", "tool schema", "tool permission", "routing rule",
            "fallback logic", "retrieval", "memory", "ingestion", "chunking",
            "embedding", "vector", "indexing", "metadata", "authorization",
            "configuration", "config", "dependency", "observability", "logs",
            "evaluation", "eval", "source document", "knowledge system"
'''
text = replace_once(text, old, new, 'governed inspection semantic coverage')

old = '''                description =
                    "Read a bounded source or test file, or the protected production branch SHA, through the configured GitHub App. This is repository inspection only; it cannot execute commands, change files, open a pull request, merge, or deploy.",
'''
new = '''                description =
                    "Inspect the configured LEGEND repository through the existing GitHub App without mutation. With no path it returns the protected production SHA plus a safe top-level repository listing; with a directory path it returns safe child metadata; with a safe text-file path it returns that file. Use it to inspect architecture, prompts, tool wiring, retrieval/memory code, workflows, manifests, tests and documentation. Secret-bearing configuration and credential material remain excluded. This cannot execute commands, change files, open a pull request, merge, or deploy.",
'''
text = replace_once(text, old, new, 'repository inspection tool description')

old = '''Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- inspect current LEGEND state when useful;
- identify weaknesses and propose high-quality teaching priorities;
'''
new = '''Your job is to:
- reason deeply about language acquisition, semantics, discourse, grammar, morphology, translation quality and curriculum strategy;
- inspect current LEGEND state, architecture, curriculum, knowledge reuse, retrieval/memory wiring, evaluation state and deployment evidence whenever the Founder asks;
- use legend_capabilities to discover the current single governed tool surface and legend_inspect_repository to discover and read the safe repository tree rather than claiming access is unavailable;
- combine independent successful reads even when another governed read reports a structured failure, and identify the exact failed tool instead of abandoning the whole diagnosis;
- identify weaknesses and propose high-quality teaching priorities;
'''
text = replace_once(text, old, new, 'teacher diagnostic mandate')
service.write_text(text)

remediation = Path('AgentPortal/Services/FounderSoftwareRemediationService.cs')
text = remediation.read_text()
text = replace_once(text,
    '    private const int MaximumFileCharacters = 60_000;\n',
    '    private const int MaximumFileCharacters = 60_000;\n'
    '    private const int MaximumReadFileCharacters = 240_000;\n',
    'repository read capacity')
text = replace_once(text,
    '        if (!string.IsNullOrWhiteSpace(path) && !IsAllowedPath(path))\n'
    '            return Failure("repository_path_not_allowed", "The requested path is outside the bounded source and test allow-list.");\n',
    '        if (!string.IsNullOrWhiteSpace(path) && !IsAllowedReadPath(path))\n'
    '            return Failure("repository_path_not_allowed", "The requested path is outside the safe read-only repository inspection boundary.");\n',
    'read-only repository boundary')

old = '''            if (string.IsNullOrWhiteSpace(path))
            {
                using var branch = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/git/ref/heads/{Uri.EscapeDataString(reference)}", null, cancellationToken);
                if (!branch.IsSuccessStatusCode)
                    return GitHubFailure("repository_reference_not_found", branch.StatusCode);

                using var branchJson = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                return new
                {
                    capability = "inspect_repository",
                    repository = options.RepositoryIdentity,
                    reference,
                    commitSha = ReadNestedString(branchJson.RootElement, "object", "sha"),
                    inspected = true
                };
            }
'''
new = '''            if (string.IsNullOrWhiteSpace(path))
            {
                using var branch = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/git/ref/heads/{Uri.EscapeDataString(reference)}", null, cancellationToken);
                if (!branch.IsSuccessStatusCode)
                    return GitHubFailure("repository_reference_not_found", branch.StatusCode);

                using var branchJson = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                using var root = await SendGitHubAsync(
                    client,
                    HttpMethod.Get,
                    $"repos/{options.RepositoryIdentity}/contents?ref={Uri.EscapeDataString(reference)}",
                    null,
                    cancellationToken);
                var entries = root.IsSuccessStatusCode
                    ? await ReadRepositoryDirectoryAsync(root, cancellationToken)
                    : Array.Empty<object>();
                return new
                {
                    capability = "inspect_repository",
                    repository = options.RepositoryIdentity,
                    reference,
                    commitSha = ReadNestedString(branchJson.RootElement, "object", "sha"),
                    entries,
                    inspected = true
                };
            }
'''
text = replace_once(text, old, new, 'root repository discovery')

old = '''            using var contentJson = await JsonDocument.ParseAsync(await content.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var encoded = ReadString(contentJson.RootElement, "content");
            var text = DecodeRepositoryContent(encoded);
            if (text is null)
                return Failure("repository_content_not_text", "The requested repository object is not a bounded UTF-8 source or test file.");

            return new
            {
                capability = "inspect_repository",
                repository = options.RepositoryIdentity,
                reference,
                path,
                sha = ReadString(contentJson.RootElement, "sha"),
                size = ReadOptionalInt(contentJson.RootElement, "size"),
                content = text,
                inspected = true
            };
'''
new = '''            using var contentJson = await JsonDocument.ParseAsync(await content.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (contentJson.RootElement.ValueKind == JsonValueKind.Array)
            {
                return new
                {
                    capability = "inspect_repository",
                    repository = options.RepositoryIdentity,
                    reference,
                    path,
                    entries = ReadRepositoryDirectory(contentJson.RootElement),
                    inspected = true
                };
            }

            var encoded = ReadString(contentJson.RootElement, "content");
            var fileText = DecodeRepositoryContent(encoded);
            if (fileText is null)
                return Failure("repository_content_not_text", "The requested repository object is not a safe bounded UTF-8 text file.");

            return new
            {
                capability = "inspect_repository",
                repository = options.RepositoryIdentity,
                reference,
                path,
                sha = ReadString(contentJson.RootElement, "sha"),
                size = ReadOptionalInt(contentJson.RootElement, "size"),
                content = fileText,
                inspected = true
            };
'''
text = replace_once(text, old, new, 'directory repository discovery')
text = replace_once(text, '!IsAllowedPath(change.Path)', '!IsAllowedRepairPath(change.Path)', 'repair path authority')

old = '''    private static bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.Contains('\\\\') || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\0'))
            return false;
        if (path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".azure/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("deploy", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("appsettings", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("launchSettings", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.StartsWith("AgentPortal/", StringComparison.Ordinal) ||
               path.StartsWith("Infrastructure/", StringComparison.Ordinal) ||
               path.StartsWith("Application/", StringComparison.Ordinal) ||
               path.StartsWith("Domain/", StringComparison.Ordinal) ||
               path.StartsWith("Shared/", StringComparison.Ordinal) ||
               path.StartsWith("AgentPortal.Tests/", StringComparison.Ordinal);
    }
'''
new = '''    private static bool IsSafeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.Contains('\\\\') || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\0'))
            return false;

        var fileName = Path.GetFileName(path);
        if (path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".azure/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("appsettings", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("launchSettings", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsAllowedReadPath(string path) =>
        IsSafeRepositoryPath(path);

    private static bool IsAllowedRepairPath(string path)
    {
        if (!IsSafeRepositoryPath(path) ||
            path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("deploy", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.StartsWith("AgentPortal/", StringComparison.Ordinal) ||
               path.StartsWith("Infrastructure/", StringComparison.Ordinal) ||
               path.StartsWith("Application/", StringComparison.Ordinal) ||
               path.StartsWith("Domain/", StringComparison.Ordinal) ||
               path.StartsWith("Shared/", StringComparison.Ordinal) ||
               path.StartsWith("AgentPortal.Tests/", StringComparison.Ordinal);
    }

    private static async Task<object[]> ReadRepositoryDirectoryAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return ReadRepositoryDirectory(document.RootElement);
    }

    private static object[] ReadRepositoryDirectory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        return root.EnumerateArray()
            .Select(item => new
            {
                name = ReadString(item, "name"),
                path = ReadString(item, "path"),
                type = ReadString(item, "type"),
                size = ReadOptionalInt(item, "size")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.path) && IsAllowedReadPath(item.path!))
            .Cast<object>()
            .ToArray();
    }
'''
text = replace_once(text, old, new, 'separate read and repair authorities')
text = replace_once(text,
    '            if (bytes.Length > MaximumFileCharacters * 4)\n'
    '                return null;\n'
    '            var text = new UTF8Encoding(false, true).GetString(bytes);\n'
    '            return text.Length <= MaximumFileCharacters ? text : null;\n',
    '            if (bytes.Length > MaximumReadFileCharacters * 4)\n'
    '                return null;\n'
    '            var text = new UTF8Encoding(false, true).GetString(bytes);\n'
    '            return text.Length <= MaximumReadFileCharacters ? text : null;\n',
    'read-only file capacity')
remediation.write_text(text)

test = Path('AgentPortal.Tests/LegendFounderAiFullDiagnosticAccessContractTests.cs')
test.write_text(r'''using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiFullDiagnosticAccessContractTests
{
    [Fact]
    public void TeacherDiagnosticIntent_CoversArchitectureKnowledgeMemoryAndObservability()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        foreach (var signal in new[]
                 {
                     "architecture", "system prompt", "tool registry", "retrieval",
                     "memory", "ingestion", "embedding", "configuration",
                     "observability", "logs", "evaluation", "knowledge system"
                 })
            Assert.Contains($"\"{signal}\"", source, StringComparison.Ordinal);
        Assert.Contains("legend_inspect_repository to discover and read the safe repository tree", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFailures_AreStructuredAndDoNotAbortIndependentInspection()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        Assert.Contains("SerializeFounderToolFailure", source, StringComparison.Ordinal);
        Assert.Contains("IsSuccessfulFounderToolOutput(toolOutput)", source, StringComparison.Ordinal);
        Assert.Contains("Independent reads may continue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new LegendFounderAiToolExecutionException(\n                call.Name,\n                \"tool_read_failed\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryInspection_HasBroaderReadAuthorityButRepairAuthorityRemainsBounded()
    {
        var source = Read("AgentPortal/Services/FounderSoftwareRemediationService.cs");
        Assert.Contains("IsAllowedReadPath", source, StringComparison.Ordinal);
        Assert.Contains("IsAllowedRepairPath", source, StringComparison.Ordinal);
        Assert.Contains("ReadRepositoryDirectory", source, StringComparison.Ordinal);
        Assert.Contains("MaximumReadFileCharacters = 240_000", source, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(\".github/\"", source, StringComparison.Ordinal);
        Assert.Contains("path.Contains(\"appsettings\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderWindows_AreExpandedWithoutRemovingFiniteSafetyEnvelope()
    {
        var source = Read("AgentPortal/Services/LegendFounderAiConversationService.cs");
        Assert.Contains("MaximumProviderConversationCharacters = 400_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumCasualOutputTokens = 8_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumReadOnlyToolSeconds = 60", source, StringComparison.Ordinal);
        Assert.Contains("MaximumToolOutputCharacters = 180_000", source, StringComparison.Ordinal);
        Assert.Contains("32_000,", source, StringComparison.Ordinal);
        Assert.Contains("64_000);", source, StringComparison.Ordinal);
        Assert.Contains("600);", source, StringComparison.Ordinal);
    }

    private static string Read(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
''')

Path('.github/workflows/apply-founder-ai-full-diagnostic-access.yml').unlink(missing_ok=True)
Path('.github/scripts/apply-founder-ai-full-diagnostic-access.py').unlink(missing_ok=True)
