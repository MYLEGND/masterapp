using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AgentPortal.Services;

public sealed partial class FounderSoftwareRemediationService
{
    private const string CandidateWorkflow = "legend-candidate-validation.yml";
    private const string CandidateWorkflowBranch = "legend/approved-changes";
    private const string CandidateProfile = "cloudflare-contracts";
    private const string CandidateVersion = "legend-candidate-validation.v1";
    private const string CandidateConfig = "FounderSoftwareRemediation:CandidateValidation:";

    // A digest binds a review; it is not authentication. Only the authenticated
    // Founder controller's explicit POST may invoke the dispatch method.
    public async Task<object> GetCandidateValidationReviewAsync(int pullRequestNumber, string headSha,
        string baseSha, string patchSha256, string trustedWorkflowSha, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null) return unavailable;
        var invalid = CandidateConfigurationError(pullRequestNumber, headSha, baseSha, patchSha256, trustedWorkflowSha);
        if (invalid is not null) return invalid;
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        var batch = await _db.FounderSoftwareRepairBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "active", cancellationToken);
        if (batch?.State != "Staged" || batch.HeadSha != headSha || batch.PullRequestNumber != pullRequestNumber)
            return Failure("batch_identity_changed", "Review the exact currently staged batch before requesting validation.");
        if (ReadCandidateEvidence(batch)?.CandidateSha == headSha)
            return Failure("candidate_validation_already_requested", "This exact candidate already has a durable validation operation; inspect its result without redispatching.");
        var evidence = NewCandidateEvidence(options, batch, baseSha, patchSha256, trustedWorkflowSha);
        return new
        {
            capability = "review_candidate_validation", repository = evidence.Repository, pullRequestNumber,
            headSha, baseSha, patchSha256, trustedWorkflowSha, profile = CandidateProfile,
            batchRevision = batch.Revision, requestId = evidence.RequestId,
            approvalActionDigest = evidence.ApprovalActionDigest, canRequest = true,
            patchDigestVerified = false, patchDigestVerification = "trusted_job_before_candidate_execution",
            mergeAuthorized = false, deploymentAuthorized = false
        };
    }

    public async Task<object> RequestCandidateValidationAsync(string actorMode, int pullRequestNumber,
        string headSha, string baseSha, string patchSha256, string trustedWorkflowSha,
        string approvalActionDigest, CancellationToken cancellationToken)
    {
        if (actorMode != "founder")
            return Failure("explicit_founder_action_required", "Only the authenticated Founder review POST can request this validation; model tools cannot dispatch it.");
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null) return unavailable;
        var invalid = CandidateConfigurationError(pullRequestNumber, headSha, baseSha, patchSha256, trustedWorkflowSha);
        if (invalid is not null) return invalid;
        if (!CandidateHex(approvalActionDigest, 64))
            return Failure("candidate_review_digest_invalid", "The exact server-generated review digest is required.");
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        if (_db.ChangeTracker.HasChanges()) return Failure("batch_context_not_clean", "Pending changes prevent a validation transaction.");
        var batch = await _db.FounderSoftwareRepairBatches.SingleOrDefaultAsync(x => x.Id == "active", cancellationToken);
        if (batch is null || batch.HeadSha != headSha || batch.PullRequestNumber != pullRequestNumber)
            return Failure("batch_identity_changed", "Only the exact staged preview can be validated.");
        var priorEvidence = ReadCandidateEvidence(batch);
        if (priorEvidence?.CandidateSha == headSha)
            return CandidateReceipt(batch, priorEvidence, replayed: true);
        if (batch.State != "Staged")
            return Failure("batch_not_editable", "An active or uncertain batch operation must be reconciled before validation.");
        var evidence = NewCandidateEvidence(options, batch, baseSha, patchSha256, trustedWorkflowSha);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(approvalActionDigest), Convert.FromHexString(evidence.ApprovalActionDigest)))
            return Failure("candidate_review_changed", "The batch revision or exact reviewed validation request changed; review it again.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var dispatchAttempted = false;
        var leased = false;
        try
        {
            using var reader = await CreateCandidateClientAsync(options, dispatch: false, inspectPull: true, deadline.Token);
            await VerifyCandidatePreconditionsAsync(reader, options, batch, evidence, deadline.Token);
            if (CandidateConfigurationError(pullRequestNumber, headSha, baseSha, patchSha256, trustedWorkflowSha) is not null)
                throw CandidateFailure("candidate_validation_configuration_changed");
            using var dispatcher = await CreateCandidateClientAsync(options, dispatch: true, inspectPull: false, deadline.Token);
            if (await RequireActiveAuthorityAsync(options, deadline.Token) is not null)
                return Failure("software_remediation_revoked", "Authority changed before validation; no dispatch was attempted.");
            // Persist before the remote write. A crash after this save leaves an
            // uncertain operation locked instead of automatically spending twice.
            batch.State = "CandidateValidationDispatching";
            batch.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5);
            batch.Revision = Guid.NewGuid().ToString("N");
            batch.UpdatedUtc = DateTime.UtcNow;
            evidence.State = "Dispatching";
            batch.CandidateValidationEvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
            await _db.SaveChangesAsync(deadline.Token);
            leased = true;
            await VerifyCandidateRefsAsync(reader, options, batch, evidence, deadline.Token);
            if (await RequireActiveAuthorityAsync(options, deadline.Token) is not null)
                throw CandidateFailure("software_remediation_revoked");
            if (CandidateConfigurationError(pullRequestNumber, headSha, baseSha, patchSha256, trustedWorkflowSha) is not null)
                throw CandidateFailure("candidate_validation_configuration_changed");
            var request = new
            {
                version = CandidateVersion, repository = evidence.Repository, baseSha, candidateSha = headSha,
                patchSha256, requestId = evidence.RequestId, profile = CandidateProfile,
                trustedWorkflowSha, approvalActionDigest
            };
            dispatchAttempted = true;
            using var response = await SendGitHubAsync(dispatcher, HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/actions/workflows/{CandidateWorkflow}/dispatches",
                new { @ref = CandidateWorkflowBranch, inputs = new { request = JsonSerializer.Serialize(request, JsonOptions) }, return_run_details = true },
                deadline.Token);
            // Remote/proxy failures do not prove the workflow was never queued.
            if (response.StatusCode != HttpStatusCode.OK) throw CandidateFailure("candidate_dispatch_outcome_unknown");
            using var result = await ReadBoundedCompletionJsonAsync(response, 16_384, deadline.Token);
            if (!result.RootElement.TryGetProperty("workflow_run_id", out var id) || !id.TryGetInt64(out var runId) || runId <= 0)
                throw CandidateFailure("candidate_dispatch_outcome_unknown");
            evidence.RunId = runId;
            evidence.State = "Requested";
            evidence.RequestedUtc = DateTime.UtcNow;
            batch.State = "CandidateValidationRequested";
            batch.LeaseUntilUtc = null;
            batch.CandidateValidationEvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
            batch.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(deadline.Token);
            return CandidateReceipt(batch, evidence, replayed: false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure("batch_busy", "Another operation changed the batch. Inspect its persisted state; no automatic retry is allowed.");
        }
        catch (Exception exception)
        {
            if (leased)
            {
                evidence.State = dispatchAttempted ? "OutcomeUnknown" : "NotDispatched";
                batch.State = dispatchAttempted ? "CandidateValidationUnknown" : "Staged";
                batch.LeaseUntilUtc = null;
                batch.UpdatedUtc = DateTime.UtcNow;
                batch.CandidateValidationEvidenceJson = dispatchAttempted ? JsonSerializer.Serialize(evidence, JsonOptions) : null;
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _db.SaveChangesAsync(recovery.Token); }
                catch (Exception) { /* Persisted Dispatching remains locked. */ }
            }
            return Failure(dispatchAttempted ? "candidate_dispatch_outcome_unknown" :
                exception is FounderSoftwareRemediationException known ? known.Code : "candidate_validation_precondition_failed",
                dispatchAttempted ? "A validation dispatch may have occurred. The batch remains locked; do not retry the paid operation." :
                "Validation prerequisites could not be verified. No workflow dispatch was attempted.");
        }
    }

    public async Task<object> GetCandidateValidationAsync(long runId, string headSha, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null) return unavailable;
        if (_configuration.GetValue<bool?>(CandidateConfig + "Enabled") != true)
            return Failure("candidate_validation_disabled", "GitHub credential issuance and candidate validation inspection are disabled.");
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        if (runId <= 0 || !CandidateHex(headSha, 40)) return Failure("candidate_run_identity_invalid", "An exact run and candidate identity is required.");
        if (_db.ChangeTracker.HasChanges()) return Failure("batch_context_not_clean", "Pending changes prevent validation reconciliation.");
        var batch = await _db.FounderSoftwareRepairBatches.SingleOrDefaultAsync(x => x.Id == "active", cancellationToken);
        var evidence = batch is null ? null : ReadCandidateEvidence(batch);
        if (batch is null || evidence is null || evidence.RunId != runId || evidence.CandidateSha != headSha || batch.HeadSha != headSha)
            return Failure("candidate_run_not_bound", "This run is not durably bound to the exact active candidate. Unknown dispatches are not guessed or retried.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var client = await CreateCandidateClientAsync(options, dispatch: false, inspectPull: false, deadline.Token);
            using var run = await ReadCompletionJsonAsync(client, $"repos/{options.RepositoryIdentity}/actions/runs/{runId}", 256 * 1024, deadline.Token);
            var value = run.RootElement;
            var workflowPath = ReadString(value, "path");
            if (ReadNestedString(value, "repository", "full_name") != evidence.Repository ||
                ReadNestedString(value, "head_repository", "full_name") != evidence.Repository ||
                ReadString(value, "head_sha") != evidence.TrustedWorkflowSha || ReadString(value, "event") != "workflow_dispatch" ||
                ReadString(value, "head_branch") != CandidateWorkflowBranch ||
                (workflowPath != ".github/workflows/" + CandidateWorkflow && workflowPath != ".github/workflows/" + CandidateWorkflow + "@" + CandidateWorkflowBranch) ||
                !value.TryGetProperty("id", out var id) || !id.TryGetInt64(out var observedId) || observedId != runId ||
                !value.TryGetProperty("run_attempt", out var attempt) || !attempt.TryGetInt32(out var runAttempt) || runAttempt != 1)
                throw CandidateFailure("candidate_run_provenance_mismatch");
            evidence.RunAttempt = runAttempt;
            evidence.Conclusion = ReadString(value, "conclusion");
            if (ReadString(value, "status") != "completed") return CandidateReceipt(batch, evidence, replayed: true);
            using var jobs = await ReadCompletionJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/actions/runs/{runId}/attempts/1/jobs?per_page=100", 1024 * 1024, deadline.Token);
            if (!jobs.RootElement.TryGetProperty("jobs", out var jobsValue) || jobsValue.ValueKind != JsonValueKind.Array ||
                !jobs.RootElement.TryGetProperty("total_count", out var total) || !total.TryGetInt32(out var jobCount) ||
                jobCount != 1 || jobsValue.GetArrayLength() != 1)
                throw CandidateFailure("candidate_job_provenance_mismatch");
            var job = jobsValue[0];
            if (ReadString(job, "name") != "validate" || ReadString(job, "head_sha") != evidence.TrustedWorkflowSha ||
                ReadString(job, "status") != "completed" || ReadString(job, "conclusion") != evidence.Conclusion ||
                !job.TryGetProperty("run_id", out var jobRun) || !jobRun.TryGetInt64(out var jobRunId) || jobRunId != runId)
                throw CandidateFailure("candidate_job_provenance_mismatch");
            if (evidence.Conclusion is not ("success" or "failure" or "cancelled" or "timed_out" or "skipped" or "action_required" or "neutral" or "stale"))
                throw CandidateFailure("candidate_run_not_terminal");
            var previouslyCompleted = evidence.State == "Completed" && batch.State == "Staged";
            evidence.State = "Completed";
            evidence.CompletedUtc ??= DateTime.UtcNow;
            evidence.GitHubJobObserved = true;
            // A trusted terminal job permits another reviewed repair. Candidate
            // artifacts remain untrusted; this never authorizes protected release.
            if (batch.State is not ("CandidateValidationRequested" or "CandidateValidationUnknown" or "CandidateValidationDispatching" or "Staged"))
                throw CandidateFailure("batch_identity_changed");
            if (!previouslyCompleted)
            {
                batch.State = "Staged";
                batch.LeaseUntilUtc = null;
                batch.Revision = Guid.NewGuid().ToString("N");
                batch.UpdatedUtc = DateTime.UtcNow;
                batch.CandidateValidationEvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
                await _db.SaveChangesAsync(deadline.Token);
            }
            return CandidateReceipt(batch, evidence, replayed: true);
        }
        catch (Exception exception)
        {
            return Failure(exception is FounderSoftwareRemediationException known ? known.Code : "candidate_validation_observation_unavailable",
                "The exact trusted validation run could not be reconciled. No dispatch, merge, or deployment was attempted.");
        }
    }

    private object? CandidateConfigurationError(int number, string head, string baseline, string patch, string trusted)
    {
        if (_configuration.GetValue<bool?>(CandidateConfig + "Enabled") != true)
            return Failure("candidate_validation_disabled", "Candidate validation is disabled until workflow review and hosted Actions capacity are approved.");
        if (number <= 0 || !CandidateHex(head, 40) || !CandidateHex(baseline, 40) || !CandidateHex(patch, 64) || !CandidateHex(trusted, 40))
            return Failure("candidate_identity_invalid", "Exact lowercase Git revisions and a patch digest are required.");
        if (_configuration[CandidateConfig + "TrustedWorkflowSha"] != trusted)
            return Failure("candidate_trusted_workflow_mismatch", "The requested workflow must equal the server-pinned reviewed workflow revision.");
        return null;
    }

    private static CandidateValidationEvidence NewCandidateEvidence(Options options, FounderSoftwareRepairBatch batch,
        string baseline, string patch, string trusted)
    {
        var evidence = new CandidateValidationEvidence
        {
            Repository = options.RepositoryIdentity, PullRequestNumber = batch.PullRequestNumber!.Value,
            BaseSha = baseline, CandidateSha = batch.HeadSha!, PatchSha256 = patch, TrustedWorkflowSha = trusted,
            BatchRevision = batch.Revision, RequestId = batch.Revision
        };
        var binding = JsonSerializer.Serialize(new
        {
            version = CandidateVersion, repository = evidence.Repository, pullRequestNumber = evidence.PullRequestNumber,
            baseSha = baseline, candidateSha = evidence.CandidateSha, patchSha256 = patch, trustedWorkflowSha = trusted,
            profile = CandidateProfile, requestId = evidence.RequestId, batchRevision = evidence.BatchRevision
        }, JsonOptions);
        evidence.ApprovalActionDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant();
        return evidence;
    }

    private async Task VerifyCandidatePreconditionsAsync(HttpClient client, Options options, FounderSoftwareRepairBatch batch,
        CandidateValidationEvidence evidence, CancellationToken token)
    {
        await VerifyCandidateRefsAsync(client, options, batch, evidence, token);
        using var commit = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/git/commits/{evidence.CandidateSha}", 256 * 1024, token);
        if (ReadString(commit.RootElement, "sha") != evidence.CandidateSha ||
            !commit.RootElement.TryGetProperty("parents", out var parents) || parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() != 1 ||
            ReadString(parents[0], "sha") != evidence.BaseSha)
            throw CandidateFailure("candidate_base_mismatch");
        if (evidence.BaseSha != evidence.TrustedWorkflowSha)
        {
            using var comparison = await ReadCompletionJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/compare/{evidence.TrustedWorkflowSha}...{evidence.BaseSha}?per_page=1", 1024 * 1024, token);
            if (ReadNestedString(comparison.RootElement, "merge_base_commit", "sha") != evidence.TrustedWorkflowSha)
                throw CandidateFailure("candidate_baseline_not_trusted_descendant");
        }
        var trustedTree = await ReadCandidateTreeAsync(client, options, evidence.TrustedWorkflowSha, token);
        var baseTree = await ReadCandidateTreeAsync(client, options, evidence.BaseSha, token);
        var candidateTree = await ReadCandidateTreeAsync(client, options, evidence.CandidateSha, token);
        var actual = CandidateChangedPaths(baseTree, candidateTree);
        var cumulative = CandidateChangedPaths(trustedTree, candidateTree);
        if (actual.Count is < 1 or > MaximumChanges || cumulative.Count > 100)
            throw CandidateFailure("candidate_change_limit_exceeded");
        foreach (var path in actual.Union(cumulative, StringComparer.Ordinal))
        {
            if (!CandidatePathAllowed(path)) throw CandidateFailure("candidate_privileged_review_required");
            foreach (var tree in new[] { trustedTree, baseTree, candidateTree })
                if (tree.TryGetValue(path, out var entry) && !entry.StartsWith("100644:", StringComparison.Ordinal))
                    throw CandidateFailure("candidate_file_mode_not_allowed");
        }
        using var workflow = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/actions/workflows/{CandidateWorkflow}", 32 * 1024, token);
        if (ReadString(workflow.RootElement, "path") != ".github/workflows/" + CandidateWorkflow ||
            ReadString(workflow.RootElement, "state") != "active")
            throw CandidateFailure("candidate_workflow_unavailable");
    }

    private static async Task VerifyCandidateRefsAsync(HttpClient client, Options options, FounderSoftwareRepairBatch batch,
        CandidateValidationEvidence evidence, CancellationToken token)
    {
        await VerifyPreviewAsync(client, options, batch, token);
        using var reference = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/git/ref/heads/{CandidateWorkflowBranch}", 32 * 1024, token);
        if (ReadNestedString(reference.RootElement, "object", "sha") != evidence.TrustedWorkflowSha)
            throw CandidateFailure("candidate_trusted_branch_moved");
    }

    private static async Task<Dictionary<string, string>> ReadCandidateTreeAsync(HttpClient client, Options options,
        string sha, CancellationToken token)
    {
        var treeSha = await ReadCommitTreeShaAsync(client, options, sha, token);
        using var document = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/git/trees/{treeSha}?recursive=1", 12 * 1024 * 1024, token);
        if (!document.RootElement.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False)
            throw CandidateFailure("candidate_tree_incomplete");
        var tree = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (ReadString(entry, "type") == "tree") continue;
            var path = ReadString(entry, "path");
            var objectSha = ReadString(entry, "sha");
            if (path is null || !CandidateHex(objectSha, 40) || !tree.TryAdd(path, ReadString(entry, "mode") + ":" + objectSha))
                throw CandidateFailure("candidate_tree_invalid");
        }
        return tree;
    }

    private static List<string> CandidateChangedPaths(Dictionary<string, string> baseline, Dictionary<string, string> candidate) =>
        baseline.Keys.Union(candidate.Keys, StringComparer.Ordinal).Where(path =>
            !baseline.TryGetValue(path, out var before) || !candidate.TryGetValue(path, out var after) || before != after).ToList();

    private static bool CandidatePathAllowed(string path)
    {
        if (path.Contains('\\') || path.Split('/').Any(part => string.IsNullOrEmpty(part) || part.StartsWith('.'))) return false;
        // Tests and fixtures are privileged: candidate code must not replace the
        // trusted acceptance assertions for the initial validation profile.
        return path is "Legend-Cloudflare/src/runtime/adapter.mjs" or "Legend-Cloudflare/src/runtime/orchestrator.mjs" or
            "Legend-Cloudflare/src/runtime/reliability.mjs" or "Infrastructure/Messaging/LegendConnectCloudflareTransport.cs" or
            "Domain/Messaging/LegendConnectContracts.cs" ||
            path.StartsWith("Docs/legend-cloudflare/", StringComparison.Ordinal) && Path.GetExtension(path) == ".md";
    }

    private async Task<HttpClient> CreateCandidateClientAsync(Options options, bool dispatch, bool inspectPull, CancellationToken token)
    {
        var vaultToken = await _credential.GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default"]), token);
        using var client = _httpClientFactory.CreateClient("FounderGitHubRemediation");
        client.BaseAddress = options.GitHubApiBaseUri;
        using var secretRequest = new HttpRequestMessage(HttpMethod.Get, KeyVaultSecretReadUri(options.PrivateKeySecretUri));
        secretRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", vaultToken.Token);
        using var secretResponse = await client.SendAsync(secretRequest, token);
        if (!secretResponse.IsSuccessStatusCode) throw CandidateFailure("github_app_key_unavailable");
        using var secret = await ReadBoundedCompletionJsonAsync(secretResponse, 32 * 1024, token);
        var key = ReadString(secret.RootElement, "value");
        if (string.IsNullOrWhiteSpace(key)) throw CandidateFailure("github_app_key_unavailable");
        var permissions = new Dictionary<string, string> { ["contents"] = "read", ["actions"] = dispatch ? "write" : "read" };
        if (inspectPull) permissions["pull_requests"] = "read";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{options.GitHubInstallationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BuildGitHubAppJwt(options.GitHubAppId, key));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("masterapp-founder-remediation", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Content = new StringContent(JsonSerializer.Serialize(new { repositories = new[] { options.Repository }, permissions }, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw CandidateFailure("candidate_github_capability_missing");
        using var issued = await ReadBoundedCompletionJsonAsync(response, 64 * 1024, token);
        var installationToken = ReadString(issued.RootElement, "token");
        if (string.IsNullOrWhiteSpace(installationToken) || !issued.RootElement.TryGetProperty("permissions", out var granted) ||
            granted.ValueKind != JsonValueKind.Object || permissions.Any(pair => ReadString(granted, pair.Key) != pair.Value) ||
            granted.EnumerateObject().Any(pair => !permissions.ContainsKey(pair.Name) && !(pair.Name == "metadata" && pair.Value.ValueKind == JsonValueKind.String && pair.Value.GetString() == "read")) ||
            !issued.RootElement.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array ||
            repositories.GetArrayLength() != 1 || ReadString(repositories[0], "full_name") != options.RepositoryIdentity)
            throw CandidateFailure("candidate_github_token_scope_unverified");
        return CreateGitHubClient(options, installationToken);
    }

    private static bool CandidateHex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CandidateValidationEvidence? ReadCandidateEvidence(FounderSoftwareRepairBatch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.CandidateValidationEvidenceJson)) return null;
        try { return JsonSerializer.Deserialize<CandidateValidationEvidence>(batch.CandidateValidationEvidenceJson, JsonOptions); }
        catch (JsonException) { throw CandidateFailure("candidate_evidence_invalid"); }
    }

    private static object CandidateReceipt(FounderSoftwareRepairBatch batch, CandidateValidationEvidence evidence, bool replayed) => new
    {
        capability = "candidate_validation", repository = evidence.Repository, pullRequestNumber = evidence.PullRequestNumber,
        headSha = evidence.CandidateSha, baseSha = evidence.BaseSha, patchSha256 = evidence.PatchSha256,
        trustedWorkflowSha = evidence.TrustedWorkflowSha, requestId = evidence.RequestId,
        approvalActionDigest = evidence.ApprovalActionDigest, profile = CandidateProfile, batchRevision = batch.Revision,
        state = evidence.State, runId = evidence.RunId, runAttempt = evidence.RunAttempt, conclusion = evidence.Conclusion,
        observedChecksPassed = evidence.GitHubJobObserved && evidence.Conclusion == "success",
        artifactVerified = false, patchDigestVerified = false, replayed,
        canEdit = batch.State == "Staged", mergeAuthorized = false, deploymentAuthorized = false
    };

    private static FounderSoftwareRemediationException CandidateFailure(string code) =>
        new(code, "The bounded candidate validation prerequisite or evidence could not be verified.");

    private sealed class CandidateValidationEvidence
    {
        public string Version { get; set; } = CandidateVersion;
        public string Repository { get; set; } = string.Empty;
        public int PullRequestNumber { get; set; }
        public string BaseSha { get; set; } = string.Empty;
        public string CandidateSha { get; set; } = string.Empty;
        public string PatchSha256 { get; set; } = string.Empty;
        public string TrustedWorkflowSha { get; set; } = string.Empty;
        public string BatchRevision { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ApprovalActionDigest { get; set; } = string.Empty;
        public string State { get; set; } = "Reviewed";
        public long? RunId { get; set; }
        public int? RunAttempt { get; set; }
        public DateTime? RequestedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string? Conclusion { get; set; }
        public bool GitHubJobObserved { get; set; }
    }
}
