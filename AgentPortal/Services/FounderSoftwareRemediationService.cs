using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

/// <summary>
/// The only application authority that can prepare or release a Founder-governed
/// software repair. It deliberately exposes repository operations as bounded
/// semantic capabilities, never as a shell, git, SQL, Azure, or token surface.
///
/// Authentication is GitHub-App installation authentication. The private key is
/// read only in memory from Key Vault with the App Service managed identity and
/// is never returned, logged, persisted, or accepted from a chat request.
/// </summary>
public interface IFounderSoftwareRemediationService
{
    Task<object> GetStatusAsync(CancellationToken cancellationToken);
    Task<object> InspectRepositoryAsync(string? path, string? gitReference, CancellationToken cancellationToken);
    Task<object> PrepareAsync(string actorMode, FounderSoftwareRepairProposal proposal, CancellationToken cancellationToken);
    Task<object> InspectValidationAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> RequestReleaseAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> ReleaseApprovedAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> VerifyDeploymentAsync(string commitSha, CancellationToken cancellationToken);
}

public sealed record FounderSoftwareRepairChange(string Path, string Content);

public sealed record FounderSoftwareRepairProposal(
    string BaseSha,
    string Title,
    string Summary,
    IReadOnlyList<FounderSoftwareRepairChange> Changes);

public sealed class FounderSoftwareRemediationService : IFounderSoftwareRemediationService
{
    private const int MaximumChanges = 6;
    private const int MaximumPathLength = 260;
    private const int MaximumFileCharacters = 60_000;
    private const int MaximumTotalCharacters = 180_000;
    private const int MaximumTitleCharacters = 160;
    private const int MaximumSummaryCharacters = 4_000;
    private static readonly TimeSpan GitHubAppJwtLifetime = TimeSpan.FromMinutes(9);
    private static readonly string[] DefaultRequiredChecks = ["Security CI / security"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FounderSoftwareRemediationService> _logger;
    private readonly TokenCredential _credential;

    public FounderSoftwareRemediationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FounderSoftwareRemediationService> logger,
        TokenCredential? credential = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
    }

    public Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        return Task.FromResult<object>(options.ValidationError is null
            ? new
            {
                capability = "founder_governed_software_remediation",
                configured = true,
                repository = options.RepositoryIdentity,
                baseBranch = options.BaseBranch,
                authentication = "GitHub App installation token via managed identity and Key Vault",
                prepareAuthority = "bounded repair branch, commit, pull request, and existing pull-request CI only",
                releaseAuthority = "explicit Founder confirmation plus exact SHA, CI, and protected-branch verification",
                directProductionAccess = false,
                arbitraryShell = false,
                arbitrarySql = false,
                rawTokenInput = false
            }
            : Unavailable(options.ValidationError));
    }

    public async Task<object> InspectRepositoryAsync(
        string? path,
        string? gitReference,
        CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);

        if (!string.IsNullOrWhiteSpace(path) && !IsAllowedPath(path))
            return Failure("repository_path_not_allowed", "The requested path is outside the bounded source and test allow-list.");

        if (!string.IsNullOrWhiteSpace(gitReference) && !IsGitReference(gitReference))
            return Failure("invalid_git_reference", "Repository inspection accepts only a branch name or immutable Git SHA.");

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            using var repository = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}", null, cancellationToken);
            if (!repository.IsSuccessStatusCode)
                return GitHubFailure("repository_inspection_failed", repository.StatusCode);

            var reference = string.IsNullOrWhiteSpace(gitReference) ? options.BaseBranch : gitReference;
            if (string.IsNullOrWhiteSpace(path))
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

            using var content = await SendGitHubAsync(
                client,
                HttpMethod.Get,
                $"repos/{options.RepositoryIdentity}/contents/{EscapeRepositoryPath(path)}?ref={Uri.EscapeDataString(reference)}",
                null,
                cancellationToken);
            if (!content.IsSuccessStatusCode)
                return GitHubFailure("repository_content_not_found", content.StatusCode);

            using var contentJson = await JsonDocument.ParseAsync(await content.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
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
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder software repair repository inspection failed without exposing credentials.");
            return Failure("repository_inspection_unavailable", "The bounded repository inspection could not be completed.");
        }
    }

    public async Task<object> PrepareAsync(
        string actorMode,
        FounderSoftwareRepairProposal proposal,
        CancellationToken cancellationToken)
    {
        if (string.Equals(actorMode, "legend", StringComparison.OrdinalIgnoreCase))
        {
            // There is not yet a canonical retained software-competency
            // authority. Refuse rather than pretending language curriculum is
            // proof of safe code-repair knowledge.
            return new
            {
                capability = "prepare_software_repair",
                knowledge = "insufficient",
                executed = false,
                escalation = "OpenAI Teacher",
                reason = "No governed retained software-repair competency has been established for Legend® Ai."
            };
        }

        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);

        var proposalError = ValidateProposal(proposal);
        if (proposalError is not null)
            return Failure("invalid_repair_proposal", proposalError);

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            var currentBaseSha = await ReadBranchShaAsync(client, options, cancellationToken);
            if (!string.Equals(currentBaseSha, proposal.BaseSha, StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    error = "base_sha_stale",
                    detail = "The production base moved after this repair was inspected. Re-inspect and prepare against the current immutable base SHA.",
                    currentBaseSha
                };
            }

            var baseTreeSha = await ReadCommitTreeShaAsync(client, options, proposal.BaseSha, cancellationToken);
            var treeEntries = new List<object>(proposal.Changes.Count);
            foreach (var change in proposal.Changes)
            {
                using var blob = await SendGitHubAsync(
                    client,
                    HttpMethod.Post,
                    $"repos/{options.RepositoryIdentity}/git/blobs",
                    new { content = change.Content, encoding = "utf-8" },
                    cancellationToken);
                if (!blob.IsSuccessStatusCode)
                    return GitHubFailure("repair_blob_creation_failed", blob.StatusCode);

                using var blobJson = await JsonDocument.ParseAsync(await blob.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var blobSha = ReadString(blobJson.RootElement, "sha");
                if (string.IsNullOrWhiteSpace(blobSha))
                    return Failure("repair_blob_creation_failed", "GitHub did not return an immutable blob identity.");

                treeEntries.Add(new { path = change.Path, mode = "100644", type = "blob", sha = blobSha });
            }

            using var tree = await SendGitHubAsync(
                client,
                HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/git/trees",
                new { base_tree = baseTreeSha, tree = treeEntries },
                cancellationToken);
            if (!tree.IsSuccessStatusCode)
                return GitHubFailure("repair_tree_creation_failed", tree.StatusCode);

            using var treeJson = await JsonDocument.ParseAsync(await tree.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var treeSha = ReadString(treeJson.RootElement, "sha");
            if (string.IsNullOrWhiteSpace(treeSha))
                return Failure("repair_tree_creation_failed", "GitHub did not return an immutable tree identity.");

            using var commit = await SendGitHubAsync(
                client,
                HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/git/commits",
                new { message = proposal.Title, tree = treeSha, parents = new[] { proposal.BaseSha } },
                cancellationToken);
            if (!commit.IsSuccessStatusCode)
                return GitHubFailure("repair_commit_creation_failed", commit.StatusCode);

            using var commitJson = await JsonDocument.ParseAsync(await commit.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var commitSha = ReadString(commitJson.RootElement, "sha");
            if (!IsCommitSha(commitSha))
                return Failure("repair_commit_creation_failed", "GitHub did not return a valid immutable repair commit SHA.");

            var branchName = $"founder-repair/{DateTimeOffset.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}";
            using var branch = await SendGitHubAsync(
                client,
                HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/git/refs",
                new { @ref = $"refs/heads/{branchName}", sha = commitSha },
                cancellationToken);
            if (!branch.IsSuccessStatusCode)
                return GitHubFailure("repair_branch_creation_failed", branch.StatusCode);

            using var pullRequest = await SendGitHubAsync(
                client,
                HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/pulls",
                new
                {
                    title = proposal.Title,
                    head = branchName,
                    @base = options.BaseBranch,
                    body = BuildPullRequestBody(proposal, commitSha!)
                },
                cancellationToken);
            if (!pullRequest.IsSuccessStatusCode)
                return GitHubFailure("repair_pull_request_creation_failed", pullRequest.StatusCode);

            using var pullJson = await JsonDocument.ParseAsync(await pullRequest.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return new
            {
                capability = "prepare_software_repair",
                prepared = true,
                repository = options.RepositoryIdentity,
                baseSha = proposal.BaseSha,
                repairCommitSha = commitSha,
                branch = branchName,
                pullRequestNumber = ReadOptionalInt(pullJson.RootElement, "number"),
                pullRequestUrl = ReadString(pullJson.RootElement, "html_url"),
                ci = "Existing pull-request Security CI is triggered by the pull request. No deployment was requested.",
                deployment = "not_authorized"
            };
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder software repair preparation failed without exposing credentials.");
            return Failure("repair_preparation_unavailable", "The bounded repair could not be prepared.");
        }
    }

    public async Task<object> InspectValidationAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);
        if (pullRequestNumber <= 0 || !IsCommitSha(headSha))
            return Failure("invalid_release_identity", "A positive pull-request number and exact immutable 40-character commit SHA are required.");

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            var validation = await ReadValidationAsync(client, options, pullRequestNumber, headSha, cancellationToken);
            return validation;
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder repair validation inspection failed without exposing credentials.");
            return Failure("repair_validation_unavailable", "The pull-request validation state could not be inspected.");
        }
    }

    public async Task<object> RequestReleaseAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken)
    {
        var validation = await InspectValidationAsync(pullRequestNumber, headSha, cancellationToken);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(validation, JsonOptions));
        return new
        {
            capability = "request_release",
            requestAccepted = true,
            pullRequestNumber,
            headSha,
            releaseRequiresFounderConfirmation = true,
            releaseRequiresExactSha = true,
            validation = document.RootElement.Clone(),
            deployment = "not_authorized"
        };
    }

    public async Task<object> ReleaseApprovedAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);
        if (pullRequestNumber <= 0 || !IsCommitSha(headSha))
            return Failure("invalid_release_identity", "A positive pull-request number and exact immutable 40-character commit SHA are required.");

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            var validation = await ReadValidationAsync(client, options, pullRequestNumber, headSha, cancellationToken);
            using var validationJson = JsonDocument.Parse(JsonSerializer.Serialize(validation, JsonOptions));
            if (!validationJson.RootElement.TryGetProperty("eligibleForProtectedMerge", out var eligible) || !eligible.GetBoolean())
            {
                return new
                {
                    error = "release_preconditions_not_met",
                    detail = "The exact repair SHA is not yet eligible for a protected merge. No merge or deployment was attempted.",
                    validation = validationJson.RootElement.Clone()
                };
            }

            var protection = await ReadBranchProtectionAsync(client, options, cancellationToken);
            if (!protection.Satisfied)
            {
                return new
                {
                    error = "protected_branch_requirements_not_verified",
                    detail = "The production branch does not prove the required pull-request, strict-status, and admin-enforcement protections. No merge was attempted.",
                    protection = protection
                };
            }

            using var merge = await SendGitHubAsync(
                client,
                HttpMethod.Put,
                $"repos/{options.RepositoryIdentity}/pulls/{pullRequestNumber}/merge",
                new { commit_title = $"Founder-approved: PR #{pullRequestNumber}", sha = headSha, merge_method = "squash" },
                cancellationToken);
            if (!merge.IsSuccessStatusCode)
                return GitHubFailure("protected_merge_rejected", merge.StatusCode);

            using var mergeJson = await JsonDocument.ParseAsync(await merge.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var merged = mergeJson.RootElement.TryGetProperty("merged", out var mergedElement) && mergedElement.GetBoolean();
            if (!merged)
                return Failure("protected_merge_rejected", ReadString(mergeJson.RootElement, "message") ?? "GitHub did not merge the exact approved SHA.");

            return new
            {
                capability = "release_approved_repair",
                released = true,
                pullRequestNumber,
                approvedHeadSha = headSha,
                mergeCommitSha = ReadString(mergeJson.RootElement, "sha"),
                deployment = "Existing protected-production push workflow was triggered; use verify_deployment for its exact run state."
            };
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder-approved release failed without exposing credentials.");
            return Failure("protected_merge_unavailable", "The protected merge could not be completed.");
        }
    }

    public async Task<object> VerifyDeploymentAsync(string commitSha, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);
        if (!IsCommitSha(commitSha))
            return Failure("invalid_deployment_identity", "An exact immutable 40-character merge commit SHA is required.");

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            using var response = await SendGitHubAsync(
                client,
                HttpMethod.Get,
                $"repos/{options.RepositoryIdentity}/actions/runs?event=push&head_sha={Uri.EscapeDataString(commitSha)}&per_page=20",
                null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return GitHubFailure("deployment_verification_unavailable", response.StatusCode);

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var runs = document.RootElement.TryGetProperty("workflow_runs", out var runsElement) && runsElement.ValueKind == JsonValueKind.Array
                ? runsElement.EnumerateArray().Select(run => new
                {
                    workflow = ReadString(run, "name"),
                    status = ReadString(run, "status"),
                    conclusion = ReadString(run, "conclusion"),
                    headSha = ReadString(run, "head_sha"),
                    url = ReadString(run, "html_url")
                }).ToArray()
                : Array.Empty<object>();

            return new
            {
                capability = "verify_deployment",
                commitSha,
                verifiedThrough = "existing GitHub protected-production deployment workflow",
                workflowRuns = runs,
                deploymentState = runs.Length == 0 ? "not_yet_observed" : "observed",
                directAzureAccess = false
            };
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder deployment verification failed without exposing credentials.");
            return Failure("deployment_verification_unavailable", "The existing deployment workflow could not be inspected.");
        }
    }

    private async Task<object> ReadValidationAsync(HttpClient client, Options options, int pullRequestNumber, string headSha, CancellationToken cancellationToken)
    {
        using var pull = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/pulls/{pullRequestNumber}", null, cancellationToken);
        if (!pull.IsSuccessStatusCode)
            return GitHubFailure("pull_request_not_found", pull.StatusCode);

        using var pullJson = await JsonDocument.ParseAsync(await pull.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var actualHeadSha = ReadNestedString(pullJson.RootElement, "head", "sha");
        var baseBranch = ReadNestedString(pullJson.RootElement, "base", "ref");
        var state = ReadString(pullJson.RootElement, "state");
        var identityMatches = string.Equals(actualHeadSha, headSha, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(baseBranch, options.BaseBranch, StringComparison.Ordinal) &&
                              string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);

        using var checks = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/commits/{headSha}/check-runs?per_page=100", null, cancellationToken);
        if (!checks.IsSuccessStatusCode)
            return GitHubFailure("repair_checks_unavailable", checks.StatusCode);

        using var checksJson = await JsonDocument.ParseAsync(await checks.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var checksByName = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (checksJson.RootElement.TryGetProperty("check_runs", out var checkRuns) && checkRuns.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checkRuns.EnumerateArray())
            {
                var name = ReadString(check, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    checksByName[name] = ReadString(check, "conclusion");
            }
        }

        var requiredChecks = options.RequiredChecks;
        var missingChecks = requiredChecks.Where(check => !checksByName.ContainsKey(check)).ToArray();
        var failedChecks = requiredChecks
            .Where(check => checksByName.TryGetValue(check, out var conclusion) && !string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var validChecks = missingChecks.Length == 0 && failedChecks.Length == 0;

        return new
        {
            capability = "inspect_validation",
            pullRequestNumber,
            requestedHeadSha = headSha,
            actualHeadSha,
            baseBranch,
            pullRequestState = state,
            exactIdentityMatches = identityMatches,
            requiredChecks,
            missingChecks,
            incompleteOrFailedChecks = failedChecks,
            checks = checksByName.Select(pair => new { name = pair.Key, conclusion = pair.Value }).ToArray(),
            eligibleForProtectedMerge = identityMatches && validChecks,
            deployment = "not_authorized"
        };
    }

    private async Task<BranchProtectionVerification> ReadBranchProtectionAsync(HttpClient client, Options options, CancellationToken cancellationToken)
    {
        using var response = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/branches/{Uri.EscapeDataString(options.BaseBranch)}/protection", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new BranchProtectionVerification(false, "branch_protection_unavailable", false, false, false, Array.Empty<string>());

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var strict = root.TryGetProperty("required_status_checks", out var requiredStatus) &&
                     requiredStatus.ValueKind == JsonValueKind.Object &&
                     requiredStatus.TryGetProperty("strict", out var strictElement) && strictElement.GetBoolean();
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        if (requiredStatus.ValueKind == JsonValueKind.Object && requiredStatus.TryGetProperty("contexts", out var contextsElement) && contextsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var context in contextsElement.EnumerateArray())
            {
                var value = context.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    contexts.Add(value);
            }
        }

        var reviews = root.TryGetProperty("required_pull_request_reviews", out var reviewElement) && reviewElement.ValueKind == JsonValueKind.Object;
        var enforceAdmins = root.TryGetProperty("enforce_admins", out var adminsElement) &&
                            adminsElement.ValueKind == JsonValueKind.Object &&
                            adminsElement.TryGetProperty("enabled", out var enabledElement) && enabledElement.GetBoolean();
        var checksCovered = options.RequiredChecks.All(contexts.Contains);
        return new BranchProtectionVerification(strict && reviews && enforceAdmins && checksCovered,
            strict && reviews && enforceAdmins && checksCovered ? "verified" : "incomplete",
            strict,
            reviews,
            enforceAdmins,
            contexts.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private async Task<HttpClient> CreateGitHubClientAsync(Options options, CancellationToken cancellationToken)
    {
        var installationToken = await CreateInstallationTokenAsync(options, cancellationToken);
        var client = _httpClientFactory.CreateClient("FounderGitHubRemediation");
        client.BaseAddress = options.GitHubApiBaseUri;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("masterapp-founder-remediation", "1.0"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task<string> CreateInstallationTokenAsync(Options options, CancellationToken cancellationToken)
    {
        var vaultToken = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://vault.azure.net/.default"]),
            cancellationToken);
        var keyVaultClient = _httpClientFactory.CreateClient("FounderGitHubRemediation");
        keyVaultClient.BaseAddress = options.GitHubApiBaseUri;
        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, KeyVaultSecretReadUri(options.PrivateKeySecretUri));
        keyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", vaultToken.Token);
        keyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var keyResponse = await keyVaultClient.SendAsync(keyRequest, cancellationToken);
        if (!keyResponse.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("github_app_key_unavailable", "The managed identity could not retrieve the configured GitHub App key from Key Vault.");

        using var keyJson = await JsonDocument.ParseAsync(await keyResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var privateKey = ReadString(keyJson.RootElement, "value");
        if (string.IsNullOrWhiteSpace(privateKey))
            throw new FounderSoftwareRemediationException("github_app_key_unavailable", "Key Vault did not return a GitHub App private key value.");

        var appJwt = BuildGitHubAppJwt(options.GitHubAppId, privateKey);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{options.GitHubInstallationId}/access_tokens");
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        tokenRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("masterapp-founder-remediation", "1.0"));
        tokenRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var tokenResponse = await keyVaultClient.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("github_app_authentication_failed", "GitHub did not issue an installation token for the configured remediation authority.");

        using var tokenJson = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var installationToken = ReadString(tokenJson.RootElement, "token");
        if (string.IsNullOrWhiteSpace(installationToken))
            throw new FounderSoftwareRemediationException("github_app_authentication_failed", "GitHub did not return an installation token.");
        return installationToken;
    }

    private static string BuildGitHubAppJwt(long appId, string privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iat = now.AddMinutes(-1).ToUnixTimeSeconds(),
            exp = now.Add(GitHubAppJwtLifetime).ToUnixTimeSeconds(),
            iss = appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        var signed = $"{header}.{payload}";
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signed}.{Base64Url(signature)}";
    }

    private static async Task<HttpResponseMessage> SendGitHubAsync(HttpClient client, HttpMethod method, string relativeUri, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadBranchShaAsync(HttpClient client, Options options, CancellationToken cancellationToken)
    {
        using var branch = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/git/ref/heads/{Uri.EscapeDataString(options.BaseBranch)}", null, cancellationToken);
        if (!branch.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("production_base_unavailable", "The configured production base branch could not be read.");
        using var document = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var sha = ReadNestedString(document.RootElement, "object", "sha");
        if (!IsCommitSha(sha))
            throw new FounderSoftwareRemediationException("production_base_unavailable", "The production branch did not return an immutable commit SHA.");
        return sha!;
    }

    private static async Task<string> ReadCommitTreeShaAsync(HttpClient client, Options options, string commitSha, CancellationToken cancellationToken)
    {
        using var commit = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/git/commits/{commitSha}", null, cancellationToken);
        if (!commit.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("repair_base_commit_not_found", "The inspected base commit is no longer available.");
        using var document = await JsonDocument.ParseAsync(await commit.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var treeSha = ReadNestedString(document.RootElement, "tree", "sha");
        if (!IsCommitSha(treeSha))
            throw new FounderSoftwareRemediationException("repair_base_commit_not_found", "The base commit did not return an immutable tree SHA.");
        return treeSha!;
    }

    private Options ReadOptions()
    {
        var enabled = _configuration.GetValue<bool?>("FounderSoftwareRemediation:Enabled") == true;
        var owner = _configuration["FounderSoftwareRemediation:RepositoryOwner"]?.Trim();
        var repository = _configuration["FounderSoftwareRemediation:RepositoryName"]?.Trim();
        var baseBranch = _configuration["FounderSoftwareRemediation:BaseBranch"]?.Trim();
        var appId = _configuration.GetValue<long?>("FounderSoftwareRemediation:GitHubAppId");
        var installationId = _configuration.GetValue<long?>("FounderSoftwareRemediation:GitHubInstallationId");
        var secretUriText = _configuration["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"]?.Trim();
        var apiBaseText = _configuration["FounderSoftwareRemediation:GitHubApiBaseUri"]?.Trim();
        var requiredChecks = _configuration.GetSection("FounderSoftwareRemediation:RequiredChecks").Get<string[]>()
            ?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        requiredChecks = requiredChecks is { Length: > 0 } ? requiredChecks : DefaultRequiredChecks;

        if (!enabled)
            return Options.Invalid("software_remediation_not_configured", "Founder-governed software remediation is disabled until a GitHub App installation is configured.");
        if (!IsRepositorySegment(owner) || !IsRepositorySegment(repository) || string.IsNullOrWhiteSpace(baseBranch) || !IsGitReference(baseBranch))
            return Options.Invalid("software_remediation_not_configured", "The bounded GitHub repository or protected base branch is not configured.");
        if (appId is not > 0 || installationId is not > 0)
            return Options.Invalid("software_remediation_not_configured", "A GitHub App and installation identity are required; raw personal tokens are not accepted.");
        if (!Uri.TryCreate(secretUriText, UriKind.Absolute, out var secretUri) || secretUri.Scheme != Uri.UriSchemeHttps || !secretUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
            return Options.Invalid("software_remediation_not_configured", "A managed-identity Azure Key Vault secret URI is required for the GitHub App private key.");
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(apiBaseText) ? "https://api.github.com/" : apiBaseText, UriKind.Absolute, out var apiBase) || apiBase.Scheme != Uri.UriSchemeHttps)
            return Options.Invalid("software_remediation_not_configured", "A secure GitHub API base URI is required.");
        if (!apiBase.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
            apiBase = new Uri(apiBase.AbsoluteUri + "/", UriKind.Absolute);

        return new Options(owner!, repository!, baseBranch!, appId.Value, installationId.Value, secretUri, apiBase, requiredChecks, null, null);
    }

    private static string? ValidateProposal(FounderSoftwareRepairProposal proposal)
    {
        if (!IsCommitSha(proposal.BaseSha))
            return "An exact immutable 40-character base SHA is required.";
        if (string.IsNullOrWhiteSpace(proposal.Title) || proposal.Title.Length > MaximumTitleCharacters)
            return $"The repair title must be between 1 and {MaximumTitleCharacters} characters.";
        if (string.IsNullOrWhiteSpace(proposal.Summary) || proposal.Summary.Length > MaximumSummaryCharacters)
            return $"The repair summary must be between 1 and {MaximumSummaryCharacters} characters.";
        if (proposal.Changes is null || proposal.Changes.Count is 0 || proposal.Changes.Count > MaximumChanges)
            return $"A repair must contain between 1 and {MaximumChanges} bounded source or test file changes.";

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;
        foreach (var change in proposal.Changes)
        {
            if (string.IsNullOrWhiteSpace(change.Path) || change.Path.Length > MaximumPathLength || !IsAllowedPath(change.Path) || !paths.Add(change.Path))
                return "Each repair path must be unique and within the bounded source and test allow-list.";
            if (change.Content is null || change.Content.Length > MaximumFileCharacters)
                return $"Each changed file must contain at most {MaximumFileCharacters} UTF-8 text characters.";
            totalCharacters += change.Content.Length;
        }

        return totalCharacters > MaximumTotalCharacters
            ? $"The bounded repair may contain at most {MaximumTotalCharacters} total text characters."
            : null;
    }

    private static bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.Contains('\\') || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\0'))
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

    private static bool IsRepositorySegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsGitReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/');

    private static bool IsCommitSha(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string EscapeRepositoryPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static Uri KeyVaultSecretReadUri(Uri secretUri)
    {
        var builder = new UriBuilder(secretUri);
        var query = builder.Query.TrimStart('?');
        if (!query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.StartsWith("api-version=", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Query = string.IsNullOrWhiteSpace(query)
                ? "api-version=7.4"
                : $"{query}&api-version=7.4";
        }

        return builder.Uri;
    }

    private static string? DecodeRepositoryContent(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace("\n", string.Empty, StringComparison.Ordinal));
            if (bytes.Length > MaximumFileCharacters * 4)
                return null;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length <= MaximumFileCharacters ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildPullRequestBody(FounderSoftwareRepairProposal proposal, string commitSha) =>
        $"Founder-governed bounded software repair.\n\nSummary: {proposal.Summary}\n\nBase SHA: `{proposal.BaseSha}`\nRepair SHA: `{commitSha}`\n\nThis pull request was prepared through the canonical Founder remediation authority. It has not been merged or deployed. Release requires explicit Founder confirmation of this exact SHA after required current-SHA validation succeeds.";

    private static object Unavailable(string? message) => new
    {
        error = "software_remediation_not_configured",
        detail = message ?? "Founder-governed software remediation is not configured.",
        rawTokenInput = false,
        directProductionAccess = false
    };

    private static object Failure(string code, string detail) => new { error = code, detail };

    private static object GitHubFailure(string code, HttpStatusCode statusCode) => new
    {
        error = code,
        githubStatus = (int)statusCode,
        detail = "GitHub rejected or could not complete the bounded operation. No broader authority was attempted."
    };

    private static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string? ReadNestedString(JsonElement element, string outer, string inner) =>
        element.TryGetProperty(outer, out var nested) && nested.ValueKind == JsonValueKind.Object ? ReadString(nested, inner) : null;

    private sealed record Options(
        string Owner,
        string Repository,
        string BaseBranch,
        long GitHubAppId,
        long GitHubInstallationId,
        Uri PrivateKeySecretUri,
        Uri GitHubApiBaseUri,
        string[] RequiredChecks,
        string? ValidationError,
        string? ValidationCode)
    {
        public string RepositoryIdentity => $"{Owner}/{Repository}";

        public static Options Invalid(string code, string message) => new("", "", "", 0, 0, new Uri("https://invalid.vault.azure.net/"), new Uri("https://api.github.com/"), DefaultRequiredChecks, message, code);
    }

    private sealed record BranchProtectionVerification(
        bool Satisfied,
        string State,
        bool StrictStatusChecks,
        bool PullRequestReviews,
        bool AdminEnforcement,
        string[] RequiredContexts);

    private sealed class FounderSoftwareRemediationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
