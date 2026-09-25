using System.Text.Json;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AgentPortal.Services;

public sealed partial class FounderSoftwareRemediationService
{
    public async Task<object> ArchiveDeployedBatchAsync(int pullRequestNumber, string headSha,
        string expectedRevision, CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null) return unavailable;
        if (_db is null) return Failure("batch_storage_unavailable", "Durable batch storage is required.");
        if (pullRequestNumber <= 0 || !IsCommitSha(headSha) || !Guid.TryParseExact(expectedRevision, "N", out _))
            return Failure("invalid_completion_identity", "An exact publication identity and batch revision are required.");
        if (_db.ChangeTracker.HasChanges())
            return Failure("batch_context_not_clean", "Unrelated pending changes prevent a completion transaction.");
        var archived = await _db.FounderSoftwareRepairBatches.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id != "active" && row.State == "WebDeploymentVerified" && row.PullRequestNumber == pullRequestNumber && row.HeadSha == headSha, cancellationToken);
        if (archived is not null) return CompletedReceipt(archived, replayed: true);
        var snapshot = await _db.FounderSoftwareRepairBatches.AsNoTracking().SingleOrDefaultAsync(row => row.Id == "active", cancellationToken);
        if (snapshot?.State != "ValidationRequested" || snapshot.PullRequestNumber != pullRequestNumber ||
            snapshot.HeadSha != headSha || snapshot.Revision != expectedRevision || !IsCommitSha(snapshot.ReviewedHeadSha))
            return Failure("batch_identity_changed", "Only the exact known publication can be archived; uncertain operations remain locked.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var hosts = CompletionHosts();
            var client = await CreateGitHubClientAsync(options, deadline.Token);
            using var pr = await ReadCompletionJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/pulls/{pullRequestNumber}", 256 * 1024, deadline.Token);
            var publicationBranch = "hotfix/publish-" + snapshot.ReviewedHeadSha;
            if (!MatchesPullRequest(pr.RootElement, options, publicationBranch, headSha, false, allowClosedObservation: true) ||
                ReadString(pr.RootElement, "state") != "closed" ||
                !pr.RootElement.TryGetProperty("merged", out var merged) || merged.ValueKind != JsonValueKind.True)
                throw CompletionFailure("publication_not_merged");
            var mergedSha = ReadString(pr.RootElement, "merge_commit_sha");
            if (!IsCommitSha(mergedSha)) throw CompletionFailure("merge_identity_missing");
            var tree = await ReadCommitTreeShaAsync(client, options, snapshot.ReviewedHeadSha!, deadline.Token);
            if (await ReadCommitTreeShaAsync(client, options, headSha, deadline.Token) != tree ||
                await ReadCommitTreeShaAsync(client, options, mergedSha!, deadline.Token) != tree)
                throw CompletionFailure("merged_tree_differs_from_reviewed_tree");
            await VerifyCompletionCoverageAsync(client, options, snapshot.BaseSha, headSha, tree, hosts.Keys.ToArray(), deadline.Token);
            using var runs = await ReadCompletionJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/actions/workflows/agentportal-production-deploy.yml/runs?head_sha={headSha}&per_page=20",
                1024 * 1024, deadline.Token);
            var runId = runs.RootElement.GetProperty("workflow_runs").EnumerateArray()
                .Where(run => ReadString(run, "head_sha") == headSha && ReadString(run, "status") == "completed" &&
                    ReadString(run, "conclusion") == "success" && ReadString(run, "event") == "pull_request" &&
                    ReadString(run, "path") == ".github/workflows/agentportal-production-deploy.yml")
                .Select(run => run.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) ? number : 0)
                .Where(number => number > 0).OrderDescending().FirstOrDefault();
            if (runId == 0) throw CompletionFailure("protected_release_success_not_observed");

            var observations = new List<VerifiedDeploymentHost>();
            var sourceTrees = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var host in hosts)
            {
                using var live = _httpClientFactory.CreateClient("FounderRuntimeProvenance");
                live.DefaultRequestHeaders.Authorization = null;
                var endpoint = new Uri(host.Value, "/api/runtime-provenance");
                using var response = await live.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri != endpoint) throw CompletionFailure("provenance_redirect_rejected");
                using var json = await ReadBoundedCompletionJsonAsync(response, 8192, deadline.Token);
                if (!json.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1 ||
                    ReadString(json.RootElement, "appIdentifier") != host.Key)
                    throw CompletionFailure("live_application_identity_mismatch");
                var source = ReadString(json.RootElement, "sourceRevision");
                if (!IsCommitSha(source)) throw CompletionFailure("live_source_revision_missing");
                if (!sourceTrees.TryGetValue(source!, out var sourceTree))
                    sourceTrees[source!] = sourceTree = await ReadCommitTreeShaAsync(client, options, source!, deadline.Token);
                if (sourceTree != tree) throw CompletionFailure("live_source_tree_mismatch");
                observations.Add(new(host.Key, source!, DateTime.UtcNow));
            }
            if (await RequireActiveAuthorityAsync(options, deadline.Token) is not null)
                throw CompletionFailure("authority_changed");
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(deadline.Token) : null;
            var active = await _db.FounderSoftwareRepairBatches.SingleAsync(row => row.Id == "active", deadline.Token);
            // The database concurrency token also protects against a change after
            // this check. Both the archive insert and active reset are one save.
            if (active.Revision != expectedRevision || active.State != snapshot.State || active.HeadSha != headSha)
                throw CompletionFailure("batch_identity_changed");
            var completed = new FounderSoftwareRepairBatch
            {
                Id = Guid.NewGuid().ToString("N"), BaseSha = snapshot.BaseSha, PreviewBranch = snapshot.PreviewBranch,
                ReviewedHeadSha = snapshot.ReviewedHeadSha, HeadSha = headSha, PullRequestNumber = pullRequestNumber,
                State = "WebDeploymentVerified", OperationId = snapshot.OperationId, UpdatedUtc = DateTime.UtcNow,
                MergedSha = mergedSha, DeployedTreeSha = tree, CompletionVerifiedUtc = DateTime.UtcNow, DeploymentRunId = runId,
                DeploymentEvidenceJson = JsonSerializer.Serialize(observations, JsonOptions)
            };
            active.BaseSha = string.Empty;
            active.PreviewBranch = BatchBranch + "/" + Guid.NewGuid().ToString("N");
            active.HeadSha = null; active.ReviewedHeadSha = null; active.PullRequestNumber = null;
            active.State = "Empty"; active.OperationId = null; active.LeaseUntilUtc = null;
            active.Revision = Guid.NewGuid().ToString("N"); active.UpdatedUtc = DateTime.UtcNow;
            active.MergedSha = null; active.DeployedTreeSha = null; active.CompletionVerifiedUtc = null;
            active.DeploymentRunId = null; active.DeploymentEvidenceJson = null;
            _db.FounderSoftwareRepairBatches.Add(completed);
            try
            {
                await _db.SaveChangesAsync(deadline.Token);
                if (transaction is not null) await transaction.CommitAsync(deadline.Token);
            }
            catch
            {
                // Do not replay a failed completion through a subsequent save.
                _db.Entry(active).State = EntityState.Detached;
                _db.Entry(completed).State = EntityState.Detached;
                throw;
            }
            return CompletedReceipt(completed, replayed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FounderSoftwareRemediationException failure) { return Failure(failure.Code, failure.Message); }
        catch (Exception) { return Failure("batch_completion_unverified", "Completion could not be verified and committed. Inspect the durable state before retrying; no remote write was requested."); }
    }

    private SortedDictionary<string, Uri> CompletionHosts()
    {
        var result = new SortedDictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var child in _configuration.GetSection("FounderSoftwareRemediation:DeploymentVerificationHosts").GetChildren())
        {
            if (child.Key.Length is 0 or > 64 || !child.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ||
                !Uri.TryCreate(child.Value, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || !origin.IsDefaultPort)
                throw CompletionFailure("deployment_verification_host_invalid");
            result.Add(child.Key, origin);
        }
        if (result.Count is 0 or > 8) throw CompletionFailure("deployment_verification_hosts_missing_or_unbounded");
        return result;
    }

    private static async Task VerifyCompletionCoverageAsync(HttpClient client, Options options, string baseSha, string headSha,
        string tree, IReadOnlyList<string> apps, CancellationToken token)
    {
        using var repository = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/git/trees/{tree}?recursive=1", 8 * 1024 * 1024, token);
        if (!repository.RootElement.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False)
            throw CompletionFailure("project_coverage_incomplete");
        var files = repository.RootElement.GetProperty("tree").EnumerateArray()
            .Where(item => ReadString(item, "type") == "blob").Select(item => ReadString(item, "path") ?? string.Empty).ToArray();
        var projects = files.Where(path => DiscoveredProjectRoot(path) is not null).ToArray();
        var configuredProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            var candidates = projects.Where(path => path.Split('/').Last() == app + ".csproj").ToArray();
            if (candidates.Length != 1) throw CompletionFailure("configured_project_identity_unresolved");
            configuredProjects.Add(candidates[0]);
        }
        using var comparison = await ReadCompletionJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/compare/{baseSha}...{headSha}?per_page=1", 1024 * 1024, token);
        if (ReadNestedString(comparison.RootElement, "merge_base_commit", "sha") != baseSha || ReadString(comparison.RootElement, "status") != "ahead")
            throw CompletionFailure("publication_lineage_unverified");
        var changes = comparison.RootElement.GetProperty("files").EnumerateArray().ToArray();
        if (changes.Length is 0 or >= 300) throw CompletionFailure("changed_file_inventory_incomplete");
        foreach (var change in changes)
        {
            var path = ReadString(change, "filename");
            if (path is null || ReadString(change, "status") != "modified") throw CompletionFailure("changed_file_inventory_unsupported");
            var owners = projects.Where(project => DiscoveredProjectRoot(project) is { } root &&
                (root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal))).ToArray();
            if (owners.Length == 0 || owners.Any(owner => !configuredProjects.Contains(owner)))
                throw CompletionFailure("changed_project_not_covered");
        }
    }

    private static async Task<JsonDocument> ReadCompletionJsonAsync(HttpClient client, string path, int maximumBytes, CancellationToken token)
    {
        using var response = await SendGitHubAsync(client, HttpMethod.Get, path, null, token);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedCompletionJsonAsync(response, maximumBytes, token);
    }

    private static async Task<JsonDocument> ReadBoundedCompletionJsonAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximumBytes) throw CompletionFailure("completion_observation_too_large");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + read > maximumBytes) throw CompletionFailure("completion_observation_too_large");
            bytes.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static FounderSoftwareRemediationException CompletionFailure(string code) =>
        new(code, "The exact configured web deployment is not verified. The batch remains available for inspection; no incident was closed.");
    private sealed record VerifiedDeploymentHost(string AppIdentifier, string SourceRevision, DateTime ObservedUtc);
    private static object CompletedReceipt(FounderSoftwareRepairBatch completed, bool replayed) => new
    {
        archived = true, replayed, archiveId = completed.Id, state = completed.State,
        mergedSha = completed.MergedSha, deployedTreeSha = completed.DeployedTreeSha,
        verificationUtc = completed.CompletionVerifiedUtc, deploymentRunId = completed.DeploymentRunId,
        verificationScope = "configured web hosts only", functionalFixVerified = false, incidentsAutomaticallyClosed = false
    };
}
