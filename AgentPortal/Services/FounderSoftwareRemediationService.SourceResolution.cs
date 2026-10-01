using System.Text.Json;
using Shared.Diagnostics;

namespace AgentPortal.Services;

public sealed record FounderRepositorySourceResolution(
    bool Ready,
    string Code,
    IReadOnlyList<string> Paths,
    bool Ambiguous);

public sealed partial class FounderSoftwareRemediationService
{
    public async Task<FounderRepositorySourceResolution> ResolveRepositorySourcePathsAsync(
        IReadOnlyList<string> sourceHints,
        string? application,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var options = ReadOptions();
        var unavailable = await RequireActiveAuthorityAsync(options, cancellationToken);
        if (unavailable is not null)
            return new(false, "software_remediation_not_configured", Array.Empty<string>(), false);
        if (!IsCommitSha(commitSha))
            return new(false, "repository_source_revision_invalid", Array.Empty<string>(), false);
        if (sourceHints.Count is < 1 or > 16)
            return new(false, "repository_source_hints_invalid", Array.Empty<string>(), false);

        var hints = sourceHints
            .Select(NormalizeSourceHint)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (hints.Length == 0 || hints.Length != sourceHints.Count)
            return new(false, "repository_source_hints_invalid", Array.Empty<string>(), false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var client = await CreateGitHubClientAsync(options, deadline.Token);
            var treeSha = await ReadCommitTreeShaAsync(client, options, commitSha, deadline.Token);
            using var tree = await ReadCompletionJsonAsync(
                client,
                $"repos/{options.RepositoryIdentity}/git/trees/{treeSha}?recursive=1",
                8 * 1024 * 1024,
                deadline.Token);
            if (!tree.RootElement.TryGetProperty("truncated", out var truncated) ||
                truncated.ValueKind != JsonValueKind.False ||
                !tree.RootElement.TryGetProperty("tree", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return new(false, "repository_source_inventory_incomplete", Array.Empty<string>(), false);

            var paths = entries.EnumerateArray()
                .Where(entry => ReadString(entry, "type") == "blob" &&
                                ReadString(entry, "mode") is "100644" or "100755")
                .Select(entry => ReadString(entry, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();

            var resolved = ResolveSafeSourceHints(hints, application, paths);
            unavailable = await RequireActiveAuthorityAsync(options, deadline.Token);
            if (unavailable is not null)
                return new(false, "software_remediation_not_configured", Array.Empty<string>(), false);
            if (resolved.Count == 0)
                return new(false, "repository_source_hint_unresolved", Array.Empty<string>(), false);
            if (resolved.Count > 12)
                return new(false, "repository_source_hint_too_ambiguous", Array.Empty<string>(), true);

            return new(true,
                resolved.Count == 1 ? "repository_source_resolved" : "repository_source_resolved_ambiguous",
                resolved,
                resolved.Count > 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, "repository_source_resolution_unavailable", Array.Empty<string>(), false);
        }
    }

    internal static IReadOnlyList<string> ResolveSafeSourceHints(
        IReadOnlyList<string> sourceHints,
        string? application,
        IEnumerable<string> repositoryPaths)
    {
        var roots = ApplicationSourceRoots(application);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in repositoryPaths)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            if (ClassifyInspectableSourcePath(normalized) != LegendSiteToolDisclosureAuthority.SafeSource)
                continue;
            if (!roots.Any(root => normalized.StartsWith(root, StringComparison.Ordinal)))
                continue;

            foreach (var hint in sourceHints)
            {
                if (!SourceHintMatches(hint, normalized)) continue;
                result.Add(normalized);
                break;
            }
        }
        return result.ToArray();
    }

    private static string? NormalizeSourceHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hint = value.Trim().Replace('\\', '/').TrimStart('/');
        if (hint.Length is 0 or > 260 || hint.Contains("..", StringComparison.Ordinal) ||
            hint.Contains('?', StringComparison.Ordinal) || hint.Contains('#', StringComparison.Ordinal))
            return null;
        if (!hint.Contains('/', StringComparison.Ordinal))
        {
            if (hint.Length > 180 ||
                hint.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')) ||
                Path.GetExtension(hint) is not (".cs" or ".cshtml"))
                return null;
            return hint;
        }
        return ClassifyInspectableSourcePath(hint) == LegendSiteToolDisclosureAuthority.SafeSource ? hint : null;
    }

    private static bool SourceHintMatches(string hint, string repositoryPath)
    {
        if (!hint.Contains('/', StringComparison.Ordinal))
            return string.Equals(Path.GetFileName(repositoryPath), hint, StringComparison.Ordinal);

        const string sharedStaticPrefix = "_content/Shared/js/";
        if (hint.StartsWith(sharedStaticPrefix, StringComparison.Ordinal))
            return string.Equals(
                repositoryPath,
                "SHARED/wwwroot/js/" + hint[sharedStaticPrefix.Length..],
                StringComparison.Ordinal);

        if (string.Equals(repositoryPath, hint, StringComparison.Ordinal) ||
            repositoryPath.EndsWith("/" + hint, StringComparison.Ordinal))
            return true;

        return repositoryPath.EndsWith("/wwwroot/" + hint, StringComparison.Ordinal);
    }

    private static string[] ApplicationSourceRoots(string? application)
    {
        var app = application?.Trim();
        var roots = new List<string> { "SHARED/", "Infrastructure/", "Domain/" };
        switch (app)
        {
            case "AgentPortal": roots.Insert(0, "AgentPortal/"); break;
            case "ClientApp": roots.Insert(0, "ClientApp/"); break;
            case "ProtectWebsite": roots.Insert(0, "Protect-Website/"); roots.Add("Legend-Design/"); break;
            case "ParfaitApp": roots.Insert(0, "ParfaitApp/"); break;
            default:
                if (!string.IsNullOrWhiteSpace(app) &&
                    app.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                    roots.Insert(0, app + "/");
                break;
        }
        return roots.Distinct(StringComparer.Ordinal).ToArray();
    }
}
