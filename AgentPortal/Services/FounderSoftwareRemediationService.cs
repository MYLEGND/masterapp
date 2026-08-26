using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AgentPortal.Models;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
    Task<object> ConnectAsync(string founderUserId, CancellationToken cancellationToken);
    Task<object> VerifyAuthorityAsync(CancellationToken cancellationToken);
    Task<object> TestRepairPreparationAsync(CancellationToken cancellationToken);
    Task<object> RevokeAsync(string founderUserId, CancellationToken cancellationToken);
    Task<object> InspectRepositoryAsync(
        FounderSoftwareRepositoryInspectionRequest request,
        CancellationToken cancellationToken);
    Task<object> PrepareAsync(string actorMode, FounderSoftwareRepairProposal proposal, CancellationToken cancellationToken);
    Task<object> InspectValidationAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> RequestReleaseAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> ReleaseApprovedAsync(int pullRequestNumber, string headSha, CancellationToken cancellationToken);
    Task<object> VerifyDeploymentAsync(string commitSha, CancellationToken cancellationToken);
}

/// <summary>
/// A small, complete UTF-8 replacement. The expected blob identity binds even
/// this compatibility path to the exact immutable file that was inspected.
/// </summary>
public sealed record FounderSoftwareRepairChange(
    string Path,
    string Content,
    string? ExpectedBlobSha = null);

/// <summary>
/// One exact replacement applied to an immutable base-file snapshot. Expected
/// text must occur once in that snapshot; a replacement may be empty.
/// </summary>
public sealed record FounderSoftwarePatchEdit(
    string ExpectedText,
    string ReplacementText);

/// <summary>
/// Ordered, bounded edits for one exact GitHub blob. The model supplies only
/// the changed fragments, never the untouched remainder of a large source.
/// </summary>
public sealed record FounderSoftwarePatchChange(
    string Path,
    string ExpectedBlobSha,
    IReadOnlyList<FounderSoftwarePatchEdit> Edits);

/// <summary>
/// Bounded repository inspection. A file is returned in full only when it is
/// small. Larger UTF-8 files expose metadata plus an explicit line range or
/// limited exact-text search result.
/// </summary>
public sealed record FounderSoftwareRepositoryInspectionRequest(
    string? Path,
    string? GitReference,
    int? StartLine = null,
    int? LineCount = null,
    string? SearchText = null,
    int? SearchContextLines = null);

public sealed record FounderSoftwareRepairProposal(
    string BaseSha,
    string Title,
    string Summary,
    IReadOnlyList<FounderSoftwareRepairChange>? Changes,
    IReadOnlyList<FounderSoftwarePatchChange>? Patches = null);

public sealed class FounderSoftwareRemediationService : IFounderSoftwareRemediationService
{
    private const int MaximumChanges = 6;
    private const int MaximumPathLength = 260;
    private const int MaximumFullReplacementCharacters = 60_000;
    private const int MaximumTotalFullReplacementCharacters = 180_000;
    private const int MaximumRepositoryFileBytes = 512_000;
    private const int MaximumResultingFileBytes = 512_000;
    private const int MaximumResultingFileCharacters = 400_000;
    private const int MaximumCumulativeFileProcessingBytes = 1_500_000;
    private const int MaximumInspectionLineCount = 200;
    private const int MaximumInspectionLineCharacters = 256;
    private const int MaximumSearchTextCharacters = 512;
    private const int MaximumSearchMatches = 12;
    private const int MaximumSearchContextLines = 4;
    private const int MaximumPatchEditsPerFile = 16;
    private const int MaximumPatchExpectedTextCharacters = 12_000;
    private const int MaximumPatchReplacementTextCharacters = 12_000;
    private const int MaximumPatchInputCharacters = 120_000;
    private const int MaximumTitleCharacters = 160;
    private const int MaximumSummaryCharacters = 4_000;
    private static readonly TimeSpan GitHubAppJwtLifetime = TimeSpan.FromMinutes(9);
    // GitHub exposes the required check-run by its job identity, not the
    // workflow display name. This is the exact current check name emitted by
    // .github/workflows/security-ci.yml; deployments fail closed if it ever
    // changes or ceases to be required on the protected production branch.
    private static readonly string[] DefaultRequiredChecks = ["security"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FounderSoftwareRemediationService> _logger;
    private readonly TokenCredential _credential;
    private readonly MasterAppDbContext? _db;

    public FounderSoftwareRemediationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FounderSoftwareRemediationService> logger)
        : this(httpClientFactory, configuration, logger, credential: null, db: null)
    {
    }

    public FounderSoftwareRemediationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FounderSoftwareRemediationService> logger,
        TokenCredential credential)
        : this(httpClientFactory, configuration, logger, credential, db: null)
    {
    }

    public FounderSoftwareRemediationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FounderSoftwareRemediationService> logger,
        MasterAppDbContext db)
        : this(httpClientFactory, configuration, logger, credential: null, db: db)
    {
    }

    private FounderSoftwareRemediationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FounderSoftwareRemediationService> logger,
        TokenCredential? credential,
        MasterAppDbContext? db)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
        _db = db;
    }

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);
        var state = await ReadStateAsync(cancellationToken);
        return ToStatus(options, state);
    }

    /// <summary>
    /// Connect never accepts a PAT, App private key, or secret URI from the
    /// browser. Infrastructure establishes the GitHub-App/Key-Vault binding;
    /// this action verifies that binding and only then clears a prior durable
    /// revocation.
    /// </summary>
    public async Task<object> ConnectAsync(string founderUserId, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);

        var verification = await VerifyAsync(options, cancellationToken);
        var state = await ReadStateAsync(cancellationToken) ?? new FounderSoftwareRemediationAuthorityState();
        if (verification.Ready)
        {
            state.IsRevoked = false;
            state.RevokedUtc = null;
            state.RevokedByUserId = null;
        }
        state.LastVerifiedUtc = DateTime.UtcNow;
        state.ProtectedProductionBranchVerified = verification.ProtectedBranchVerified;
        state.SecurityCiVerified = verification.SecurityCiVerified;
        state.RepairPreparationVerified = verification.RepairPreparationReady;
        state.LastVerificationCode = verification.Code;
        state.LastVerificationDetail = TrimForStorage(verification.Detail, 500);
        state.UpdatedUtc = DateTime.UtcNow;
        await SaveStateAsync(state, cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<object> VerifyAuthorityAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);

        var verification = await VerifyAsync(options, cancellationToken);
        var state = await ReadStateAsync(cancellationToken) ?? new FounderSoftwareRemediationAuthorityState();
        state.LastVerifiedUtc = DateTime.UtcNow;
        state.ProtectedProductionBranchVerified = verification.ProtectedBranchVerified;
        state.SecurityCiVerified = verification.SecurityCiVerified;
        state.RepairPreparationVerified = verification.RepairPreparationReady;
        state.LastVerificationCode = verification.Code;
        state.LastVerificationDetail = TrimForStorage(verification.Detail, 500);
        state.UpdatedUtc = DateTime.UtcNow;
        await SaveStateAsync(state, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<object> TestRepairPreparationAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);

        var verification = await VerifyAsync(options, cancellationToken);
        return new
        {
            capability = "test_repair_preparation",
            dryRun = true,
            repository = options.RepositoryIdentity,
            wouldCreateIsolatedRepairBranch = verification.RepairPreparationReady,
            wouldOpenPullRequest = verification.RepairPreparationReady,
            productionChanged = false,
            repositoryChanged = false,
            detail = verification.Detail,
            error = verification.Ready ? null : verification.Code
        };
    }

    public async Task<object> RevokeAsync(string founderUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(founderUserId))
            return Failure("founder_identity_required", "A Founder identity is required to revoke software remediation.");

        var state = await ReadStateAsync(cancellationToken) ?? new FounderSoftwareRemediationAuthorityState();
        state.IsRevoked = true;
        state.RevokedUtc = DateTime.UtcNow;
        state.RevokedByUserId = founderUserId;
        state.UpdatedUtc = DateTime.UtcNow;
        await SaveStateAsync(state, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<object> InspectRepositoryAsync(
        FounderSoftwareRepositoryInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return unavailable;

        if (!string.IsNullOrWhiteSpace(request.Path) && !IsAllowedPath(request.Path))
            return Failure("repository_path_not_allowed", "The requested path is outside the bounded source and test allow-list.");

        if (!string.IsNullOrWhiteSpace(request.GitReference) && !IsGitReference(request.GitReference))
            return Failure("invalid_git_reference", "Repository inspection accepts only a branch name or immutable Git SHA.");

        var inspectionError = ValidateInspectionRequest(request);
        if (inspectionError is not null)
            return Failure(inspectionError.Code, inspectionError.Detail);

        try
        {
            var client = await CreateGitHubClientAsync(options, cancellationToken);
            using var repository = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}", null, cancellationToken);
            if (!repository.IsSuccessStatusCode)
                return GitHubFailure("repository_inspection_failed", repository.StatusCode);

            var reference = string.IsNullOrWhiteSpace(request.GitReference) ? options.BaseBranch : request.GitReference;
            var commitSha = await ResolveInspectionCommitShaAsync(client, options, reference!, cancellationToken);
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return new
                {
                    capability = "inspect_repository",
                    repository = options.RepositoryIdentity,
                    reference,
                    commitSha,
                    inspected = true
                };
            }

            var file = await ReadRepositoryFileAsync(
                client,
                options,
                request.Path,
                commitSha,
                MaximumRepositoryFileBytes,
                cancellationToken);
            var inspection = BuildInspection(file, request);

            return new
            {
                capability = "inspect_repository",
                repository = options.RepositoryIdentity,
                reference,
                commitSha,
                path = request.Path,
                blobSha = file.BlobSha,
                byteCount = file.ByteCount,
                characterCount = file.Text.Length,
                lineCount = file.Lines.Count,
                fullFileReturned = inspection.FullFileReturned,
                truncated = inspection.Truncated,
                content = inspection.Content,
                lineRange = inspection.LineRange,
                search = inspection.Search,
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
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return unavailable;

        var proposalError = ValidateProposal(proposal);
        if (proposalError is not null)
            return Failure(proposalError.Code, proposalError.Detail);

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
            var preparedFiles = await PrepareFilesInMemoryAsync(
                client,
                options,
                proposal,
                cancellationToken);

            // Every base file, expected blob, UTF-8 payload, patch occurrence,
            // output bound, and cumulative processing limit passed above. Only
            // now may GitHub receive a new blob; no validation failure can
            // leave an orphan blob, tree, branch, commit, or pull request.
            var treeEntries = new List<object>(preparedFiles.Count);
            foreach (var prepared in preparedFiles)
            {
                using var blob = await SendGitHubAsync(
                    client,
                    HttpMethod.Post,
                    $"repos/{options.RepositoryIdentity}/git/blobs",
                    new { content = prepared.ResultingText, encoding = "utf-8" },
                    cancellationToken);
                if (!blob.IsSuccessStatusCode)
                    return GitHubFailure("repair_blob_creation_failed", blob.StatusCode);

                using var blobJson = await JsonDocument.ParseAsync(await blob.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var blobSha = ReadString(blobJson.RootElement, "sha");
                if (!IsGitBlobSha(blobSha))
                    return Failure("repair_blob_creation_failed", "GitHub did not return an immutable blob identity.");

                treeEntries.Add(new { path = prepared.Path, mode = "100644", type = "blob", sha = blobSha });
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
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return unavailable;
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
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return unavailable;
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
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return unavailable;
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
        var session = await CreateInstallationSessionAsync(options, cancellationToken);
        return CreateGitHubClient(options, session.Token);
    }

    private HttpClient CreateGitHubClient(Options options, string installationToken)
    {
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

    private async Task<GitHubInstallationSession> CreateInstallationSessionAsync(Options options, CancellationToken cancellationToken)
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
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tokenJson.RootElement.TryGetProperty("permissions", out var permissionsElement) && permissionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in permissionsElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    permissions[property.Name] = property.Value.GetString()!;
            }
        }
        return new GitHubInstallationSession(installationToken, permissions);
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

    private static async Task<string> ResolveInspectionCommitShaAsync(
        HttpClient client,
        Options options,
        string reference,
        CancellationToken cancellationToken)
    {
        if (IsCommitSha(reference))
            return reference;

        using var branch = await SendGitHubAsync(
            client,
            HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/git/ref/heads/{Uri.EscapeDataString(reference)}",
            null,
            cancellationToken);
        if (!branch.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("repository_reference_not_found", "The requested repository branch could not be resolved to an immutable commit.");
        using var document = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var sha = ReadNestedString(document.RootElement, "object", "sha");
        if (!IsCommitSha(sha))
            throw new FounderSoftwareRemediationException("repository_reference_not_found", "The requested repository branch did not return an immutable commit SHA.");
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

    private async Task<object?> RequireActiveAuthorityAsync(Options options, CancellationToken cancellationToken)
    {
        if (options.ValidationError is not null)
            return Unavailable(options.ValidationError);
        var state = await ReadStateAsync(cancellationToken);
        return state?.IsRevoked == true
            ? Unavailable("Founder software remediation has been revoked. Connect and verify the configured GitHub App authority before a bounded repair can be prepared or released.")
            : null;
    }

    private async Task<FounderSoftwareRemediationAuthorityState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (_db is null)
            return null;
        return await _db.FounderSoftwareRemediationAuthorityStates
            .SingleOrDefaultAsync(item => item.ScopeKey == "Global", cancellationToken);
    }

    private async Task SaveStateAsync(FounderSoftwareRemediationAuthorityState state, CancellationToken cancellationToken)
    {
        if (_db is null)
            return;
        if (state.Id == Guid.Empty)
            state.Id = Guid.NewGuid();
        if (_db.Entry(state).State == EntityState.Detached)
            _db.FounderSoftwareRemediationAuthorityStates.Add(state);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private FounderSoftwareRemediationStatusSnapshot ToStatus(
        Options options,
        FounderSoftwareRemediationAuthorityState? state)
    {
        var revoked = state?.IsRevoked == true;
        var ready = !revoked && state?.RepairPreparationVerified == true;
        var status = revoked ? "REVOKED" : ready ? "READY" : "AWAITING VERIFICATION";
        var detail = revoked
            ? "Founder software remediation is disabled. It cannot prepare, merge, or release a repair until the configured GitHub App binding is reconnected and verified."
            : ready
                ? state?.LastVerificationDetail ?? "The configured authority was verified without exposing a credential. Repair preparation remains bounded to an isolated branch and pull request; release always requires a separate explicit Founder approval of the exact SHA."
                : state?.LastVerificationDetail ?? "The GitHub App binding is configured but has not yet passed the read-only Key Vault, repository-permission, protected-branch, and security-CI verification.";
        return new FounderSoftwareRemediationStatusSnapshot(
            true,
            revoked,
            options.RepositoryIdentity,
            "GitHub App installation",
            "Azure Key Vault",
            "App Service Managed Identity",
            false,
            state?.ProtectedProductionBranchVerified == true,
            state?.SecurityCiVerified == true,
            ready,
            true,
            status,
            detail,
            state?.LastVerifiedUtc,
            options.RequiredChecks);
    }

    private async Task<AuthorityVerification> VerifyAsync(Options options, CancellationToken cancellationToken)
    {
        try
        {
            var session = await CreateInstallationSessionAsync(options, cancellationToken);
            var client = CreateGitHubClient(options, session.Token);
            using var repository = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}", null, cancellationToken);
            if (!repository.IsSuccessStatusCode)
                return AuthorityVerification.Failed("repository_permission_unverified", "The GitHub App installation could not read the configured repository.");

            var baseSha = await ReadBranchShaAsync(client, options, cancellationToken);
            var protection = await ReadBranchProtectionAsync(client, options, cancellationToken);
            var checkStates = await ReadCheckStatesAsync(client, options, baseSha, cancellationToken);
            var requiredChecksSuccessful = options.RequiredChecks.All(check =>
                checkStates.TryGetValue(check, out var conclusion) &&
                string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase));
            var permissionsSufficient = HasAtLeast(session.Permissions, "contents", "write") &&
                HasAtLeast(session.Permissions, "pull_requests", "write") &&
                HasAtLeast(session.Permissions, "checks", "read");
            var protectedVerified = protection.Satisfied;
            var ready = permissionsSufficient && protectedVerified && requiredChecksSuccessful;
            var grantedPermissions = session.Permissions.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}:{item.Value}").ToArray();
            var currentChecks = checkStates.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}:{item.Value ?? "pending"}").ToArray();
            var detail = $"Repository permissions required: contents:write, pull_requests:write, checks:read. Granted: " +
                $"{(grantedPermissions.Length == 0 ? "not reported by GitHub" : string.Join(", ", grantedPermissions))}. " +
                $"Protected branch: {protection.State}. Required checks: {string.Join(", ", options.RequiredChecks)}. " +
                $"Current checks: {(currentChecks.Length == 0 ? "none reported" : string.Join(", ", currentChecks))}. " +
                (ready
                    ? "Managed identity and the sealed Key Vault credential were verified; repair preparation is bounded and release still requires explicit Founder approval of the exact SHA."
                    : "No repair branch, pull request, merge, deployment, or production-data mutation was attempted.");
            return new AuthorityVerification(
                ready,
                ready ? "verified" : "authority_requirements_not_verified",
                detail,
                protectedVerified,
                requiredChecksSuccessful,
                permissionsSufficient,
                ready,
                new
                {
                    requiredPermissions = new[] { "contents:write", "pull_requests:write", "checks:read" },
                    grantedPermissions,
                    requiredChecks = options.RequiredChecks,
                    currentChecks,
                    protectedBranch = protection.State,
                    baseSha
                });
        }
        catch (FounderSoftwareRemediationException exception)
        {
            return AuthorityVerification.Failed(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Founder remediation authority verification failed without exposing a credential.");
            return AuthorityVerification.Failed("authority_verification_unavailable", "The configured remediation authority could not be verified. No broader authority was attempted.");
        }
    }

    private static async Task<Dictionary<string, string?>> ReadCheckStatesAsync(
        HttpClient client,
        Options options,
        string commitSha,
        CancellationToken cancellationToken)
    {
        using var response = await SendGitHubAsync(client, HttpMethod.Get, $"repos/{options.RepositoryIdentity}/commits/{commitSha}/check-runs?per_page=100", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("security_ci_unavailable", "The current production Security CI state could not be read.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("check_runs", out var checks) && checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                var name = ReadString(check, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    result[name] = ReadString(check, "conclusion");
            }
        }
        return result;
    }

    private static bool HasAtLeast(IReadOnlyDictionary<string, string> permissions, string key, string minimum)
    {
        if (!permissions.TryGetValue(key, out var actual))
            return false;
        var value = actual.ToLowerInvariant();
        return minimum switch
        {
            "read" => value is "read" or "write" or "admin",
            "write" => value is "write" or "admin",
            _ => false
        };
    }

    private static RemediationFailure? ValidateProposal(FounderSoftwareRepairProposal proposal)
    {
        if (!IsCommitSha(proposal.BaseSha))
            return new("invalid_repair_proposal", "An exact immutable 40-character base commit SHA is required.");
        if (string.IsNullOrWhiteSpace(proposal.Title) || proposal.Title.Length > MaximumTitleCharacters)
            return new("invalid_repair_proposal", $"The repair title must be between 1 and {MaximumTitleCharacters} characters.");
        if (string.IsNullOrWhiteSpace(proposal.Summary) || proposal.Summary.Length > MaximumSummaryCharacters)
            return new("invalid_repair_proposal", $"The repair summary must be between 1 and {MaximumSummaryCharacters} characters.");

        var fullChanges = proposal.Changes ?? Array.Empty<FounderSoftwareRepairChange>();
        var patches = proposal.Patches ?? Array.Empty<FounderSoftwarePatchChange>();
        if ((fullChanges.Count == 0 && patches.Count == 0) ||
            (fullChanges.Count > 0 && patches.Count > 0) ||
            fullChanges.Count + patches.Count > MaximumChanges)
        {
            return new("invalid_repair_proposal", $"A repair must contain one bounded full-file or patch mode with between 1 and {MaximumChanges} changed files.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var totalFullReplacementCharacters = 0;
        foreach (var change in fullChanges)
        {
            if (!IsValidRepairPath(change.Path, paths))
                return new("invalid_repair_proposal", "Each repair path must be unique and within the bounded source and test allow-list.");
            if (!IsGitBlobSha(change.ExpectedBlobSha))
                return new("invalid_repair_proposal", "Every full-file replacement requires the exact inspected 40-character Git blob SHA.");
            if (change.Content is null || change.Content.Length > MaximumFullReplacementCharacters || !IsStrictUtf8(change.Content))
                return new("repository_content_exceeds_bounded_limit", $"Each complete replacement must be strict UTF-8 and contain at most {MaximumFullReplacementCharacters} characters.");
            totalFullReplacementCharacters += change.Content.Length;
        }

        if (totalFullReplacementCharacters > MaximumTotalFullReplacementCharacters)
            return new("repository_content_exceeds_bounded_limit", $"Complete replacement input may contain at most {MaximumTotalFullReplacementCharacters} total characters.");

        var totalPatchInputCharacters = 0;
        foreach (var patch in patches)
        {
            if (!IsValidRepairPath(patch.Path, paths))
                return new("invalid_repair_proposal", "Each repair path must be unique and within the bounded source and test allow-list.");
            if (!IsGitBlobSha(patch.ExpectedBlobSha))
                return new("invalid_repair_proposal", "Every patch requires the exact inspected 40-character Git blob SHA.");
            if (patch.Edits is null || patch.Edits.Count == 0 || patch.Edits.Count > MaximumPatchEditsPerFile)
                return new("patch_limit_exceeded", $"Each file patch must contain between 1 and {MaximumPatchEditsPerFile} ordered edits.");

            foreach (var edit in patch.Edits)
            {
                if (string.IsNullOrEmpty(edit.ExpectedText) ||
                    edit.ExpectedText.Length > MaximumPatchExpectedTextCharacters ||
                    edit.ReplacementText is null ||
                    edit.ReplacementText.Length > MaximumPatchReplacementTextCharacters ||
                    !IsStrictUtf8(edit.ExpectedText) ||
                    !IsStrictUtf8(edit.ReplacementText))
                {
                    return new("patch_limit_exceeded", $"Each patch expected and replacement fragment must be strict UTF-8 and at most {MaximumPatchExpectedTextCharacters} and {MaximumPatchReplacementTextCharacters} characters respectively.");
                }

                totalPatchInputCharacters += edit.ExpectedText.Length + edit.ReplacementText.Length;
            }
        }

        return totalPatchInputCharacters > MaximumPatchInputCharacters
            ? new RemediationFailure("patch_limit_exceeded", $"Patch input may contain at most {MaximumPatchInputCharacters} total characters.")
            : null;
    }

    private static RemediationFailure? ValidateInspectionRequest(FounderSoftwareRepositoryInspectionRequest request)
    {
        var rangeRequested = request.StartLine.HasValue || request.LineCount.HasValue;
        var searchRequested = !string.IsNullOrWhiteSpace(request.SearchText) || request.SearchContextLines.HasValue;
        if (rangeRequested && searchRequested)
            return new("repository_inspection_request_invalid", "Choose either a bounded line range or an exact-text search, not both.");
        if (rangeRequested && (!request.StartLine.HasValue || !request.LineCount.HasValue ||
            request.StartLine.Value < 1 || request.LineCount.Value < 1 || request.LineCount.Value > MaximumInspectionLineCount))
        {
            return new("repository_inspection_request_invalid", $"A line range requires a positive start line and between 1 and {MaximumInspectionLineCount} lines.");
        }
        if (searchRequested && (string.IsNullOrWhiteSpace(request.SearchText) ||
            request.SearchText.Length > MaximumSearchTextCharacters ||
            !IsStrictUtf8(request.SearchText) ||
            request.SearchContextLines is < 0 or > MaximumSearchContextLines))
        {
            return new("repository_inspection_request_invalid", $"An exact UTF-8 search may contain at most {MaximumSearchTextCharacters} characters and 0 to {MaximumSearchContextLines} context lines.");
        }

        return null;
    }

    private async Task<IReadOnlyList<PreparedRepositoryFile>> PrepareFilesInMemoryAsync(
        HttpClient client,
        Options options,
        FounderSoftwareRepairProposal proposal,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedRepositoryFile>();
        var processedBytes = 0;

        foreach (var change in proposal.Changes ?? Array.Empty<FounderSoftwareRepairChange>())
        {
            var baseFile = await ReadRepositoryFileAsync(client, options, change.Path, proposal.BaseSha, MaximumRepositoryFileBytes, cancellationToken);
            processedBytes = AddProcessedBytes(processedBytes, baseFile.ByteCount);
            EnsureExpectedBlob(change.Path, change.ExpectedBlobSha!, baseFile.BlobSha);
            if (baseFile.Text.Length > MaximumFullReplacementCharacters)
                throw new FounderSoftwareRemediationException("repository_content_exceeds_bounded_limit", "Complete replacement is allowed only for a bounded small UTF-8 file. Use exact blob-bound patches for a larger file.");
            EnsureResultingFileWithinBounds(change.Content);
            processedBytes = AddProcessedBytes(processedBytes, StrictUtf8ByteCount(change.Content));
            prepared.Add(new PreparedRepositoryFile(change.Path, change.Content));
        }

        foreach (var patch in proposal.Patches ?? Array.Empty<FounderSoftwarePatchChange>())
        {
            var baseFile = await ReadRepositoryFileAsync(client, options, patch.Path, proposal.BaseSha, MaximumRepositoryFileBytes, cancellationToken);
            processedBytes = AddProcessedBytes(processedBytes, baseFile.ByteCount);
            EnsureExpectedBlob(patch.Path, patch.ExpectedBlobSha, baseFile.BlobSha);
            var resulting = ApplyPatchEdits(baseFile.Text, patch.Edits, patch.Path);
            EnsureResultingFileWithinBounds(resulting);
            processedBytes = AddProcessedBytes(processedBytes, StrictUtf8ByteCount(resulting));
            prepared.Add(new PreparedRepositoryFile(patch.Path, resulting));
        }

        return prepared;
    }

    private async Task<RepositoryFile> ReadRepositoryFileAsync(
        HttpClient client,
        Options options,
        string path,
        string commitSha,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var content = await SendGitHubAsync(
            client,
            HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/contents/{EscapeRepositoryPath(path)}?ref={Uri.EscapeDataString(commitSha)}",
            null,
            cancellationToken);
        if (!content.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("repository_content_not_found", "The requested repository file was not found at the immutable base commit.");

        using var contentJson = await JsonDocument.ParseAsync(await content.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!string.Equals(ReadString(contentJson.RootElement, "type"), "file", StringComparison.OrdinalIgnoreCase))
            throw new FounderSoftwareRemediationException("repository_content_not_text", "The requested repository object is not a source or test file.");
        var blobSha = ReadString(contentJson.RootElement, "sha");
        if (!IsGitBlobSha(blobSha))
            throw new FounderSoftwareRemediationException("repository_content_not_text", "The requested repository object has no immutable Git blob identity.");
        var declaredSize = ReadOptionalInt(contentJson.RootElement, "size");
        if (declaredSize is < 0 || declaredSize > maximumBytes)
            throw new FounderSoftwareRemediationException("repository_content_exceeds_bounded_limit", $"The requested repository file exceeds the {maximumBytes}-byte bounded processing limit.");

        using var blob = await SendGitHubAsync(
            client,
            HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/git/blobs/{blobSha}",
            null,
            cancellationToken);
        if (!blob.IsSuccessStatusCode)
            throw new FounderSoftwareRemediationException("repository_content_not_found", "The immutable repository file blob could not be read.");

        using var blobJson = await JsonDocument.ParseAsync(await blob.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!string.Equals(ReadString(blobJson.RootElement, "encoding"), "base64", StringComparison.OrdinalIgnoreCase))
            throw new FounderSoftwareRemediationException("repository_content_not_utf8", "The repository file is not a strict UTF-8 text blob.");
        var decoded = DecodeStrictUtf8(ReadString(blobJson.RootElement, "content"), maximumBytes);
        return new RepositoryFile(blobSha!, decoded.Text, decoded.ByteCount, SplitLines(decoded.Text));
    }

    private static void EnsureExpectedBlob(string path, string expectedBlobSha, string actualBlobSha)
    {
        if (!string.Equals(expectedBlobSha, actualBlobSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new FounderSoftwareRemediationException(
                "expected_blob_sha_stale",
                $"The inspected blob for {path} changed at the immutable base commit. Re-inspect the file before preparing a repair.");
        }
    }

    private static string ApplyPatchEdits(
        string source,
        IReadOnlyList<FounderSoftwarePatchEdit> edits,
        string path)
    {
        var locations = new List<(int Start, int Length, string Replacement)>();
        foreach (var edit in edits)
        {
            var first = source.IndexOf(edit.ExpectedText, StringComparison.Ordinal);
            if (first < 0)
                throw new FounderSoftwareRemediationException("patch_expected_text_missing", $"A required exact patch fragment was not found in {path}.");
            if (source.IndexOf(edit.ExpectedText, first + 1, StringComparison.Ordinal) >= 0)
                throw new FounderSoftwareRemediationException("patch_expected_text_ambiguous", $"A required exact patch fragment occurs more than once in {path}.");
            locations.Add((first, edit.ExpectedText.Length, edit.ReplacementText));
        }

        for (var index = 1; index < locations.Count; index++)
        {
            if (locations[index].Start < locations[index - 1].Start)
                throw new FounderSoftwareRemediationException("patch_edits_reordered", $"Patch edits for {path} must be supplied in source order.");
            if (locations[index].Start < locations[index - 1].Start + locations[index - 1].Length)
                throw new FounderSoftwareRemediationException("patch_edits_overlap", $"Patch edits for {path} overlap and cannot be applied atomically.");
        }

        var builder = new StringBuilder(source.Length);
        var cursor = 0;
        foreach (var location in locations)
        {
            builder.Append(source, cursor, location.Start - cursor);
            builder.Append(location.Replacement);
            cursor = location.Start + location.Length;
        }
        builder.Append(source, cursor, source.Length - cursor);
        return builder.ToString();
    }

    private static void EnsureResultingFileWithinBounds(string text)
    {
        if (!IsStrictUtf8(text))
            throw new FounderSoftwareRemediationException("repository_content_not_utf8", "A resulting repository file must be strict UTF-8 text.");
        if (text.Length > MaximumResultingFileCharacters || StrictUtf8ByteCount(text) > MaximumResultingFileBytes)
            throw new FounderSoftwareRemediationException("resulting_file_limit_exceeded", $"A resulting repository file may contain at most {MaximumResultingFileCharacters} characters and {MaximumResultingFileBytes} UTF-8 bytes.");
    }

    private static int AddProcessedBytes(int total, int next)
    {
        if (next > MaximumCumulativeFileProcessingBytes - total)
            throw new FounderSoftwareRemediationException("patch_limit_exceeded", $"Cumulative repository-file processing may not exceed {MaximumCumulativeFileProcessingBytes} bytes.");
        return total + next;
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

    // A Git blob SHA currently has the same lexical form as a commit SHA, but
    // it is deliberately validated and named separately in the authority and
    // tool contract so callers cannot conflate the two immutable identities.
    private static bool IsGitBlobSha(string? value) => IsCommitSha(value);

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

    private static DecodedUtf8 DecodeStrictUtf8(string? encoded, int maximumBytes)
    {
        try
        {
            var bytes = string.IsNullOrWhiteSpace(encoded)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(encoded.Replace("\n", string.Empty, StringComparison.Ordinal));
            if (bytes.Length > maximumBytes)
                throw new FounderSoftwareRemediationException("repository_content_exceeds_bounded_limit", $"The requested repository file exceeds the {maximumBytes}-byte bounded processing limit.");
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (!IsStrictUtf8(text))
                throw new FounderSoftwareRemediationException("repository_content_not_utf8", "The repository file is not a strict UTF-8 source or test file.");
            return new DecodedUtf8(text, bytes.Length);
        }
        catch (FounderSoftwareRemediationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new FounderSoftwareRemediationException("repository_content_not_utf8", "The repository file is not a strict UTF-8 source or test file.");
        }
    }

    private static bool IsStrictUtf8(string text)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(text);
            return !text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t');
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static int StrictUtf8ByteCount(string text) => new UTF8Encoding(false, true).GetByteCount(text);

    private static bool IsValidRepairPath(string path, ISet<string> paths) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= MaximumPathLength &&
        IsAllowedPath(path) &&
        paths.Add(path);

    private static IReadOnlyList<string> SplitLines(string text) => text.Split('\n');

    private static RepositoryInspection BuildInspection(
        RepositoryFile file,
        FounderSoftwareRepositoryInspectionRequest request)
    {
        var rangeRequested = request.StartLine.HasValue;
        var searchRequested = !string.IsNullOrWhiteSpace(request.SearchText);
        if (!rangeRequested && !searchRequested && file.Text.Length <= MaximumFullReplacementCharacters)
        {
            return new RepositoryInspection(
                true,
                false,
                file.Text,
                null,
                null);
        }

        if (rangeRequested)
        {
            var start = request.StartLine!.Value;
            var count = request.LineCount!.Value;
            var selected = SelectLines(file.Lines, start, count);
            return new RepositoryInspection(
                false,
                start > 1 || start - 1 + selected.Count < file.Lines.Count,
                null,
                new
                {
                    startLine = start,
                    requestedLineCount = count,
                    returnedLineCount = selected.Count,
                    totalLineCount = file.Lines.Count,
                    beforeTruncated = start > 1,
                    afterTruncated = start - 1 + selected.Count < file.Lines.Count,
                    lines = selected
                },
                null);
        }

        if (searchRequested)
        {
            var context = request.SearchContextLines ?? 0;
            var matches = new List<object>();
            var offset = 0;
            var moreMatches = false;
            while (offset <= file.Text.Length - request.SearchText!.Length)
            {
                var index = file.Text.IndexOf(request.SearchText, offset, StringComparison.Ordinal);
                if (index < 0)
                    break;
                if (matches.Count == MaximumSearchMatches)
                {
                    moreMatches = true;
                    break;
                }

                var line = LineNumberAt(file.Text, index);
                matches.Add(new
                {
                    line,
                    contextStartLine = Math.Max(1, line - context),
                    contextEndLine = Math.Min(file.Lines.Count, line + context),
                    lines = SelectLines(file.Lines, Math.Max(1, line - context), Math.Min(file.Lines.Count, line + context) - Math.Max(1, line - context) + 1)
                });
                offset = index + Math.Max(1, request.SearchText.Length);
            }

            return new RepositoryInspection(
                false,
                // A search result is always a deliberately bounded view, even
                // when it found every occurrence.  Do not let callers mistake
                // a contextual excerpt for the complete source file.
                true,
                null,
                null,
                new
                {
                    exactText = request.SearchText,
                    contextLines = context,
                    returnedMatchCount = matches.Count,
                    matchesTruncated = moreMatches,
                    matches
                });
        }

        // Oversized text remains inspectable, but metadata alone is returned
        // until the caller supplies a bounded range or exact-text search.
        return new RepositoryInspection(false, true, null, null, new
        {
            mode = "metadata_only",
            reason = "repository_content_exceeds_bounded_limit",
            detail = $"This UTF-8 file is larger than the {MaximumFullReplacementCharacters}-character full-return limit. Inspect a bounded line range or exact-text search using its immutable blob SHA."
        });
    }

    private static IReadOnlyList<RepositoryInspectionLine> SelectLines(
        IReadOnlyList<string> lines,
        int startLine,
        int count)
    {
        if (startLine > lines.Count)
            return Array.Empty<RepositoryInspectionLine>();

        return lines
            .Skip(startLine - 1)
            .Take(count)
            .Select((line, index) => new RepositoryInspectionLine(
                startLine + index,
                line.Length > MaximumInspectionLineCharacters
                    ? line[..MaximumInspectionLineCharacters]
                    : line,
                line.Length > MaximumInspectionLineCharacters))
            .ToArray();
    }

    private static int LineNumberAt(string text, int characterIndex)
    {
        var line = 1;
        for (var index = 0; index < characterIndex; index++)
        {
            if (text[index] == '\n')
                line++;
        }
        return line;
    }

    private sealed record RemediationFailure(string Code, string Detail);

    private sealed record DecodedUtf8(string Text, int ByteCount);

    private sealed record RepositoryFile(
        string BlobSha,
        string Text,
        int ByteCount,
        IReadOnlyList<string> Lines);

    private sealed record PreparedRepositoryFile(string Path, string ResultingText);

    private sealed record RepositoryInspection(
        bool FullFileReturned,
        bool Truncated,
        string? Content,
        object? LineRange,
        object? Search);

    private sealed record RepositoryInspectionLine(
        int LineNumber,
        string Text,
        bool Truncated);

    private static string TrimForStorage(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

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

    private sealed record GitHubInstallationSession(string Token, IReadOnlyDictionary<string, string> Permissions);

    private sealed record AuthorityVerification(
        bool Ready,
        string Code,
        string Detail,
        bool ProtectedBranchVerified,
        bool SecurityCiVerified,
        bool PermissionsSufficient,
        bool RepairPreparationReady,
        object Result)
    {
        public static AuthorityVerification Failed(string code, string detail) => new(
            false,
            code,
            detail,
            false,
            false,
            false,
            false,
            new { error = code, detail, directProductionAccess = false, rawTokenInput = false });
    }

    private sealed class FounderSoftwareRemediationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
