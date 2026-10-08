using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPortal.Services;

// This is evidence collection through the existing GitHub App authority.
// It cannot approve, dispatch, merge, retry, or repair anything.
public sealed record FounderReleaseFailureEvidence(
    long RunId,
    int SourcePullRequest,
    string CandidateSha,
    string AuthoritySha,
    string FailureStage);

public sealed partial class FounderSoftwareRemediationService
{
    private static readonly Regex ReleaseIdentity = new(
        @"\ALEGEND release pr=([1-9][0-9]{0,8}) candidate=([a-f0-9]{40}) authority=([a-f0-9]{40})\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    // Existing hosted engineering passes may run every minute. Evidence scanning
    // is observational and never owns a second release scheduler or mutation lease.
    private static long _nextReleaseEvidenceReadUtcTicks;

    public async Task<IReadOnlyList<FounderReleaseFailureEvidence>> ReadRecentFailedReleaseEvidenceAsync(
        CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        if (options.ValidationError is not null ||
            !string.Equals(options.BaseBranch, "legend/approved-changes", StringComparison.Ordinal))
            return [];

        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _nextReleaseEvidenceReadUtcTicks);
        if (now < previous ||
            Interlocked.CompareExchange(
                ref _nextReleaseEvidenceReadUtcTicks, now + TimeSpan.FromMinutes(5).Ticks,
                previous) != previous)
            return [];

        // Honor global Founder revocation even for read-only model handoff.
        if (await RequireActiveAuthorityAsync(options, cancellationToken) is not null)
            return [];

        using var client = await CreateGitHubClientAsync(options, cancellationToken);
        var observations = new List<FounderReleaseFailureEvidence>(5);
        using var document = await ReadReleaseEvidenceJsonAsync(client,
            $"repos/{options.RepositoryIdentity}/actions/runs?branch={Uri.EscapeDataString(options.BaseBranch)}&event=workflow_dispatch&per_page=100",
            cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("workflow_runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("trusted_release_run_inventory_unavailable");

        foreach (var run in runs.EnumerateArray())
        {
            if (observations.Count == 5)
                break;
            var parsed = ParseFailedRunMetadata(run, options.RepositoryIdentity, options.BaseBranch);
            if (parsed is null)
                continue;

            // An authenticated run is not sufficient: the source PR and exact
            // protected merge ancestry must also be proven before ingestion.
            using var prDoc = await ReadReleaseEvidenceJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/pulls/{parsed.SourcePullRequest}",
                cancellationToken);
            var pr = prDoc.RootElement;
            if (ReadString(pr, "merged_at") is null ||
                ReadNestedString(pr, "base", "ref") != options.BaseBranch ||
                ReadNestedString(pr, "head", "sha") != parsed.CandidateSha)
                continue;

            var merge = ReadString(pr, "merge_commit_sha");
            if (!IsCommitSha(merge))
                continue;
            if (!string.Equals(merge, parsed.AuthoritySha, StringComparison.Ordinal))
            {
                using var lineage = await ReadReleaseEvidenceJsonAsync(client,
                    $"repos/{options.RepositoryIdentity}/compare/{merge}...{parsed.AuthoritySha}",
                    cancellationToken);
                if (ReadString(lineage.RootElement, "status") != "ahead")
                    continue;
            }

            using var jobsDoc = await ReadReleaseEvidenceJsonAsync(client,
                $"repos/{options.RepositoryIdentity}/actions/runs/{parsed.RunId}/jobs?filter=latest&per_page=100",
                cancellationToken);
            var jobsRoot = jobsDoc.RootElement;
            if (!jobsRoot.TryGetProperty("total_count", out var total) ||
                !total.TryGetInt32(out var totalCount) || totalCount > 100 ||
                !jobsRoot.TryGetProperty("jobs", out var jobs) ||
                jobs.ValueKind != JsonValueKind.Array)
                continue;
            var releaseJobs = jobs.EnumerateArray().Where(job =>
                ReadString(job, "name") == "release").ToArray();
            if (releaseJobs.Length != 1 ||
                ReadString(releaseJobs[0], "status") != "completed" ||
                ReadString(releaseJobs[0], "conclusion") != "failure")
                continue;

            observations.Add(parsed with
            {
                FailureStage = ClassifyReleaseStep(releaseJobs[0])
            });
        }

        return observations;
    }

    internal static FounderReleaseFailureEvidence? ParseFailedRunMetadata(
        JsonElement run, string repository, string approvedBranch)
    {
        if (ReadString(run, "event") != "workflow_dispatch" ||
            ReadString(run, "status") != "completed" ||
            ReadString(run, "conclusion") != "failure" ||
            ReadString(run, "head_branch") != approvedBranch ||
            ReadNestedString(run, "head_repository", "full_name") != repository ||
            ReadString(run, "path")?.Split('@')[0] !=
                ".github/workflows/all-intentional-direct-release-20260918.yml")
            return null;
        var title = ReadString(run, "display_title");
        if (title is null)
            return null;
        var match = ReleaseIdentity.Match(title);
        if (!match.Success ||
            ReadString(run, "head_sha") != match.Groups[3].Value ||
            !long.TryParse(run.TryGetProperty("id", out var id) ? id.ToString() : "",
                out var runId) || runId <= 0 ||
            !int.TryParse(match.Groups[1].Value, out var pr) || pr <= 0)
            return null;

        // Terminal workflow failure is *not* evidence the candidate is live.
        return new FounderReleaseFailureEvidence(
            runId, pr, match.Groups[2].Value, match.Groups[3].Value, "UNCLASSIFIED");
    }

    internal static string ClassifyReleaseStep(JsonElement releaseJob)
    {
        if (!releaseJob.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
            return "UNCLASSIFIED";
        foreach (var step in steps.EnumerateArray())
        {
            if (ReadString(step, "conclusion") != "failure")
                continue;
            return ReadString(step, "name") switch
            {
                "Synchronize canonical pre-publication resource lanes" => "PREPUBLICATION",
                "Verify current live base before publication" => "LIVE_BASE",
                "Prepare complete immutable release transaction" => "TRANSACTION_PREPARE",
                "Reconcile complete immutable release transaction" => "TRANSACTION_RECONCILE",
                "Verify deployed application revisions and browser contracts" => "LIVE_PROOF",
                _ => "UNCLASSIFIED"
            };
        }
        return "UNCLASSIFIED";
    }

    private static async Task<JsonDocument> ReadReleaseEvidenceJsonAsync(
        HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await SendGitHubAsync(
            client, HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("trusted_release_evidence_unavailable");
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }
}
