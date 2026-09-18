using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed partial class FounderSoftwareRemediationService
{
    private const string BatchBranch = "hotfix/staging-batch";

    public async Task<object> ReconcileBatchAsync(CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null) return unavailable;
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        var batch = await _db.FounderSoftwareRepairBatches.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == "active", cancellationToken);
        if (batch is null) return new { state = "Empty", readOnly = true, verified = false, writesUnlocked = false };
        if (!IsCommitSha(batch.HeadSha) || batch.PullRequestNumber is not > 0)
            return new { state = batch.State, readOnly = true, verified = false, writesUnlocked = false,
                detail = "The durable operation has no complete remote identity. It remains locked; no write was retried." };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var remoteIdentityMatches = false;
        var publicationPullRequestIdentityMatches = false;
        string? observedPullRequestState = null;
        bool? observedPullRequestMerged = null;
        object? workflowObservations = null;
        try
        {
            var client = await CreateGitHubClientAsync(options, deadline.Token);
            if (batch.State == "ValidationRequested")
            {
                using var response = await SendGitHubAsync(client, HttpMethod.Get,
                    $"repos/{options.RepositoryIdentity}/pulls/{batch.PullRequestNumber}", null, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var pr = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
                observedPullRequestState = ReadString(pr.RootElement, "state") is "open" or "closed" ? ReadString(pr.RootElement, "state") : "unknown";
                observedPullRequestMerged = pr.RootElement.TryGetProperty("merged", out var merged) &&
                    merged.ValueKind is JsonValueKind.True or JsonValueKind.False ? merged.GetBoolean() : null;
                var branch = ReadNestedString(pr.RootElement, "head", "ref");
                const string prefix = "hotfix/publish-";
                if (branch is null || !branch.StartsWith(prefix, StringComparison.Ordinal) || !IsCommitSha(branch[prefix.Length..]) ||
                    !MatchesPullRequest(pr.RootElement, options, branch, batch.HeadSha!, draft: false, allowClosedObservation: true))
                    throw new InvalidOperationException("Publication identity changed.");
                publicationPullRequestIdentityMatches = true;
                using var reference = await SendGitHubAsync(client, HttpMethod.Get,
                    $"repos/{options.RepositoryIdentity}/git/ref/heads/{branch}", null, deadline.Token);
                reference.EnsureSuccessStatusCode();
                using var refJson = await JsonDocument.ParseAsync(await reference.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
                remoteIdentityMatches = ReadNestedString(refJson.RootElement, "object", "sha") == batch.HeadSha;
            }
            else
            {
                await VerifyPreviewAsync(client, options, batch, deadline.Token);
                remoteIdentityMatches = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Only bounded observation: uncertainty must not unlock another write. */ }
        // A merged PR or deleted publication ref must not hide workflow evidence
        // for the immutable head already retained by the publication authority.
        if (batch.State is "ValidationRequested" or "Publishing" or "OutcomeUnknown")
        {
            try { workflowObservations = await VerifyDeploymentAsync(batch.HeadSha!, deadline.Token); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* An unavailable observation never establishes live deployment. */ }
        }
        // Even an expired lease may hide a still-completing remote operation.
        // Matching an older head is not proof that a pending write did not occur.
        return new { state = batch.State, headSha = batch.HeadSha, pullRequestNumber = batch.PullRequestNumber,
            readOnly = true, remoteIdentityMatches, verified = false, writesUnlocked = false,
            publicationPullRequestIdentityMatches, observedPullRequestState, observedPullRequestMerged,
            workflowObservations, detail = "Remote observations do not prove deployment or completion of an uncertain write. No operation was retried or unlocked." };
    }

    private async Task<object> StageBatchAsync(Options options, FounderSoftwareRepairProposal proposal, CancellationToken token)
    {
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required before any repository write.");
        var operation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal, JsonOptions))));
        var batch = await _db.Set<FounderSoftwareRepairBatch>().SingleOrDefaultAsync(x => x.Id == "active", token);
        if (batch is null)
        {
            batch = new FounderSoftwareRepairBatch { UpdatedUtc = DateTime.UtcNow };
            _db.Add(batch);
            try { await _db.SaveChangesAsync(token); }
            catch (DbUpdateException) { return Failure("batch_busy", "Another operation initialized this batch; inspect its status before retrying."); }
        }
        if (batch.State == "Staged" && batch.OperationId == operation)
        {
            try
            {
                var client = await CreateGitHubClientAsync(options, token);
                await VerifyPreviewAsync(client, options, batch, token);
                return BatchReceipt(batch, replayed: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return Failure("batch_replay_unverified", "The saved preview no longer has a verified matching branch and open draft pull request."); }
        }
        if (batch.State is not ("Empty" or "Staged"))
            return Failure("batch_not_editable", "The batch is publishing or has an uncertain operation. Reconcile its exact GitHub state before further writes.");
        var priorHead = batch.HeadSha;
        var priorState = batch.State;
        var priorOperation = batch.OperationId;
        if (priorHead is not null && priorHead != proposal.BaseSha)
            return Failure("base_sha_stale", "The proposed patch must be reviewed against the current batch head.");
        batch.State = "Staging";
        batch.OperationId = operation;
        batch.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5);
        batch.Revision = Guid.NewGuid().ToString("N");
        batch.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return Failure("batch_busy", "Another operation changed this batch. No GitHub write was attempted."); }
        var remoteWriteAttempted = false;
        try
        {
            var client = await CreateGitHubClientAsync(options, token);
            var production = await ReadBranchShaAsync(client, options, token);
            if (priorHead is null && production != proposal.BaseSha)
                throw new InvalidOperationException("Production base changed.");
            var refPath = $"repos/{options.RepositoryIdentity}/git/ref/heads/{BatchBranch}";
            using (var branch = await SendGitHubAsync(client, HttpMethod.Get, refPath, null, token))
            {
                if (priorHead is null && branch.StatusCode != HttpStatusCode.NotFound)
                    throw new InvalidOperationException("An untracked staging branch already exists.");
                if (priorHead is not null)
                {
                    branch.EnsureSuccessStatusCode();
                    using var doc = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(token), cancellationToken: token);
                    if (ReadNestedString(doc.RootElement, "object", "sha") != priorHead)
                        throw new InvalidOperationException("Staging branch moved.");
                }
            }
            if (batch.PullRequestNumber is not null)
            {
                await VerifyPreviewAsync(client, options, batch, token);
            }
            var unavailable = await RequireActiveAuthorityAsync(options, token);
            if (unavailable is not null) throw new InvalidOperationException("Authority changed.");
            var tree = await ReadCommitTreeShaAsync(client, options, proposal.BaseSha, token);
            var sourceModes = await VerifyDiscoveredPathsAsync(client, options, tree, proposal.Changes, token);
            var entries = new List<object>();
            remoteWriteAttempted = true;
            foreach (var change in proposal.Changes)
            {
                var blob = await GitWriteAsync(client, options, "git/blobs", new { content = change.Content, encoding = "utf-8" }, token);
                entries.Add(new { path = change.Path, mode = sourceModes[change.Path], type = "blob", sha = blob.GetProperty("sha").GetString() });
            }
            var newTree = await GitWriteAsync(client, options, "git/trees", new { base_tree = tree, tree = entries }, token);
            var commit = await GitWriteAsync(client, options, "git/commits", new
            {
                message = proposal.Title + "\n\nLEGEND operation: " + operation,
                tree = newTree.GetProperty("sha").GetString(), parents = new[] { proposal.BaseSha }
            }, token);
            var sha = commit.GetProperty("sha").GetString();
            if (!IsCommitSha(sha)) throw new InvalidOperationException("Missing immutable commit.");
            if (await RequireActiveAuthorityAsync(options, token) is not null) throw new InvalidOperationException("Authority changed.");
            using var reference = await SendGitHubAsync(client, priorHead is null ? HttpMethod.Post : HttpMethod.Patch,
                $"repos/{options.RepositoryIdentity}/git/refs" + (priorHead is null ? "" : "/heads/" + BatchBranch),
                priorHead is null ? new { @ref = "refs/heads/" + BatchBranch, sha } : (object)new { sha, force = false }, token);
            reference.EnsureSuccessStatusCode();
            batch.BaseSha = priorHead is null ? production : batch.BaseSha;
            batch.HeadSha = sha;
            if (batch.PullRequestNumber is null)
            {
                var pr = await GitWriteAsync(client, options, "pulls", new
                {
                    title = "Founder reviewed repair batch", head = BatchBranch, @base = options.BaseBranch,
                    body = BuildPullRequestBody(proposal, sha!), draft = true
                }, token);
                batch.PullRequestNumber = pr.GetProperty("number").GetInt32();
            }
            batch.State = "Staged";
            batch.LeaseUntilUtc = null;
            batch.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(token);
            return BatchReceipt(batch, replayed: false);
        }
        catch (Exception)
        {
            // A timeout does not prove a remote write failed. No automatic retry or deletion.
            batch.State = remoteWriteAttempted ? "OutcomeUnknown" : priorState;
            if (!remoteWriteAttempted) batch.OperationId = priorOperation;
            batch.LeaseUntilUtc = null;
            batch.UpdatedUtc = DateTime.UtcNow;
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _db.SaveChangesAsync(recovery.Token); } catch (Exception) { /* Persisted Staging remains non-editable. */ }
            return Failure(remoteWriteAttempted ? "batch_outcome_unknown" : "batch_precondition_changed",
                "No deployment was requested. Inspect the persisted operation and GitHub branch before retrying.");
        }
    }

    private async Task<object> PublishBatchAsync(Options options, int number, string headSha, CancellationToken token)
    {
        var client = await CreateGitHubClientAsync(options, token);
        var protection = await ReadBranchProtectionAsync(client, options, token);
        if (!protection.Satisfied)
            return Failure("protected_branch_requirements_not_verified", "Existing release protections could not be verified; no publication attempted.");
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        var batch = await _db.Set<FounderSoftwareRepairBatch>().SingleOrDefaultAsync(x => x.Id == "active", token);
        if (batch?.State != "Staged" || batch.HeadSha != headSha || batch.PullRequestNumber != number)
            return Failure("batch_identity_changed", "Publish requires the exact staged, reviewed batch identity.");
        if (await ReadBranchShaAsync(client, options, token) != batch.BaseSha)
            return Failure("production_base_changed", "Production advanced. Reconcile and revalidate the batch before publication.");
        batch.State = "Publishing";
        batch.Revision = Guid.NewGuid().ToString("N");
        batch.UpdatedUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return Failure("batch_busy", "Another batch operation won the publication claim."); }
        try
        {
            await VerifyPreviewAsync(client, options, batch, token);
            var publicationBranch = "hotfix/publish-" + headSha;
            using (var existing = await SendGitHubAsync(client, HttpMethod.Get,
                $"repos/{options.RepositoryIdentity}/git/ref/heads/{publicationBranch}", null, token))
                if (existing.StatusCode != HttpStatusCode.NotFound)
                    throw new InvalidOperationException("Publication branch already exists or could not be inspected.");
            var tree = await ReadCommitTreeShaAsync(client, options, headSha, token);
            if (await RequireActiveAuthorityAsync(options, token) is not null) throw new InvalidOperationException("Authority changed.");
            // The mutable preview is never made ready or used to trigger release.
            // A publication ref starts at exactly the explicitly reviewed head.
            using var reference = await SendGitHubAsync(client, HttpMethod.Post,
                $"repos/{options.RepositoryIdentity}/git/refs", new { @ref = "refs/heads/" + publicationBranch, sha = headSha }, token);
            reference.EnsureSuccessStatusCode();
            if (await RequireActiveAuthorityAsync(options, token) is not null) throw new InvalidOperationException("Authority changed.");
            var publication = await GitWriteAsync(client, options, "pulls", new
            {
                title = "Founder approved repair publication",
                head = publicationBranch, @base = options.BaseBranch, draft = false,
                body = "Explicit publication of reviewed head " + headSha + ". The existing protected workflow remains the only release authority."
            }, token);
            var publicationNumber = publication.GetProperty("number").GetInt32();
            if (publicationNumber <= 0 || !MatchesPullRequest(publication, options, publicationBranch, headSha, draft: false))
                throw new InvalidOperationException("Publication pull request identity was not confirmed.");
            if (await RequireActiveAuthorityAsync(options, token) is not null) throw new InvalidOperationException("Authority changed.");
            var marker = await GitWriteAsync(client, options, "git/commits", new
            {
                message = "Founder requested validation and publication of exact batch tree " + tree,
                tree, parents = new[] { headSha }
            }, token);
            var publishSha = marker.GetProperty("sha").GetString();
            if (!IsCommitSha(publishSha)) throw new InvalidOperationException("Missing publication commit.");
            if (await RequireActiveAuthorityAsync(options, token) is not null) throw new InvalidOperationException("Authority changed.");
            using var update = await SendGitHubAsync(client, HttpMethod.Patch,
                $"repos/{options.RepositoryIdentity}/git/refs/heads/{publicationBranch}", new { sha = publishSha, force = false }, token);
            update.EnsureSuccessStatusCode();
            batch.HeadSha = publishSha;
            batch.PullRequestNumber = publicationNumber;
            batch.State = "ValidationRequested";
            batch.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(token);
            return new { capability = "release_approved_repair", released = false, publicationRequested = true,
                approvedHeadSha = headSha, publishedHeadSha = publishSha, treeSha = tree, pullRequestNumber = publicationNumber,
                publicationBranch,
                deployment = "Existing protected workflow requested; checks, merge and live deployment are not yet verified." };
        }
        catch (Exception)
        {
            batch.State = "OutcomeUnknown";
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _db.SaveChangesAsync(recovery.Token); } catch (Exception) { /* Publishing remains locked. */ }
            return Failure("batch_publication_outcome_unknown", "Inspect the exact PR and workflow before retrying. No direct merge was attempted.");
        }
    }

    private static bool MatchesPullRequest(JsonElement pr, Options options, string branch, string head, bool draft,
        bool allowClosedObservation = false) =>
        ReadNestedString(pr, "head", "sha") == head && ReadNestedString(pr, "head", "ref") == branch &&
        ReadNestedString(pr, "base", "ref") == options.BaseBranch &&
        (ReadString(pr, "state") == "open" || allowClosedObservation && ReadString(pr, "state") == "closed") &&
        pr.TryGetProperty("draft", out var draftValue) && draftValue.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        draftValue.GetBoolean() == draft && pr.TryGetProperty("head", out var headValue) &&
        ReadNestedString(headValue, "repo", "full_name") == options.RepositoryIdentity &&
        pr.TryGetProperty("base", out var baseValue) && ReadNestedString(baseValue, "repo", "full_name") == options.RepositoryIdentity;

    private static async Task VerifyPreviewAsync(HttpClient client, Options options, FounderSoftwareRepairBatch batch, CancellationToken token)
    {
        if (!IsCommitSha(batch.HeadSha) || batch.PullRequestNumber is not > 0)
            throw new InvalidOperationException("Missing preview identity.");
        using var branch = await SendGitHubAsync(client, HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/git/ref/heads/{BatchBranch}", null, token);
        branch.EnsureSuccessStatusCode();
        using var branchDoc = await JsonDocument.ParseAsync(await branch.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (ReadNestedString(branchDoc.RootElement, "object", "sha") != batch.HeadSha)
            throw new InvalidOperationException("Preview branch moved.");
        using var pr = await SendGitHubAsync(client, HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/pulls/{batch.PullRequestNumber}", null, token);
        pr.EnsureSuccessStatusCode();
        using var prDoc = await JsonDocument.ParseAsync(await pr.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!MatchesPullRequest(prDoc.RootElement, options, BatchBranch, batch.HeadSha!, draft: true))
            throw new InvalidOperationException("Only the matching open draft preview is accepted.");
    }

    private static async Task<IReadOnlyDictionary<string, string>> VerifyDiscoveredPathsAsync(HttpClient client, Options options, string tree,
        IReadOnlyList<FounderSoftwareRepairChange> changes, CancellationToken token)
    {
        using var response = await SendGitHubAsync(client, HttpMethod.Get,
            $"repos/{options.RepositoryIdentity}/git/trees/{tree}?recursive=1", null, token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!document.RootElement.TryGetProperty("truncated", out var truncated) || truncated.GetBoolean())
            throw new InvalidOperationException("Repository discovery is incomplete.");
        var files = document.RootElement.GetProperty("tree").EnumerateArray()
            .Where(x => ReadString(x, "type") == "blob" && ReadString(x, "mode") is "100644" or "100755")
            .ToDictionary(x => ReadString(x, "path") ?? string.Empty, x => ReadString(x, "mode")!, StringComparer.Ordinal);
        var roots = files.Keys.Select(DiscoveredProjectRoot).Where(root => root is not null).ToArray();
        foreach (var change in changes)
            if (!files.ContainsKey(change.Path) || !roots.Any(root => root == string.Empty || change.Path.StartsWith(root + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException("Repair must change an existing source file in a discovered project.");
        return files;
    }

    private static string? DiscoveredProjectRoot(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = path[(slash + 1)..];
        if (path.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal))
        {
            var bundle = path[..slash];
            var parent = bundle.LastIndexOf('/');
            return parent < 0 ? string.Empty : bundle[..parent];
        }
        return name.EndsWith(".csproj", StringComparison.Ordinal) || name is "settings.gradle.kts" or "settings.gradle" or "package.json"
            ? slash < 0 ? string.Empty : path[..slash]
            : null;
    }

    private static async Task<JsonElement> GitWriteAsync(HttpClient client, Options options, string resource, object body, CancellationToken token)
    {
        using var response = await SendGitHubAsync(client, HttpMethod.Post, $"repos/{options.RepositoryIdentity}/{resource}", body, token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return document.RootElement.Clone();
    }

    private static object BatchReceipt(FounderSoftwareRepairBatch batch, bool replayed) => new
    {
        capability = "prepare_software_repair", prepared = true, state = "STAGED_UNPUBLISHED",
        baseSha = batch.BaseSha, repairCommitSha = batch.HeadSha, branch = BatchBranch,
        pullRequestNumber = batch.PullRequestNumber, replayed,
        ci = "Draft batch does not authorize production execution.", deployment = "not_requested"
    };
}
