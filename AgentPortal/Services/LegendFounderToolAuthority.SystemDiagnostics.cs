using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPortal.Services;

internal sealed partial class LegendFounderToolAuthority
{
    private async Task<object> ReadSanitizedSystemHealthAsync(CancellationToken cancellationToken)
    {
        if (_authorizationScopes is null)
            return new
            {
                ok = false,
                error = "system_health_authority_unavailable",
                disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.OperationalMetadata
            };

        using var scope = _authorizationScopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var now = DateTime.UtcNow;
        const int maximumRows = 5000;
        var rows = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .Where(row => row.ExpiresUtc > now)
            .Select(row => new
            {
                row.AppIdentifier,
                row.Platform,
                row.Category,
                row.StatusCode,
                row.GitCommitHash,
                row.ReleaseVerified,
                row.Occurrences
            })
            .Take(maximumRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > maximumRows;
        if (truncated) rows = rows.Take(maximumRows).ToList();

        var surfaces = rows
            .GroupBy(row => new { row.AppIdentifier, row.Platform })
            .OrderBy(group => group.Key.AppIdentifier, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Platform, StringComparer.Ordinal)
            .Select(group => new
            {
                application = SafeHealthSymbol(group.Key.AppIdentifier),
                platform = SafeHealthSymbol(group.Key.Platform),
                incidents = group.Sum(row => Math.Max(1L, row.Occurrences)),
                categories = group
                    .GroupBy(row => row.Category)
                    .OrderBy(category => category.Key, StringComparer.Ordinal)
                    .Select(category => new
                    {
                        category = SafeHealthSymbol(category.Key),
                        count = category.Sum(row => Math.Max(1L, row.Occurrences))
                    })
                    .Take(16)
                    .ToArray(),
                statusCodes = group
                    .Where(row => row.StatusCode is >= 100 and <= 599)
                    .GroupBy(row => row.StatusCode!.Value)
                    .OrderBy(code => code.Key)
                    .Select(code => new
                    {
                        statusCode = code.Key,
                        count = code.Sum(row => Math.Max(1L, row.Occurrences))
                    })
                    .Take(16)
                    .ToArray(),
                verifiedSourceRevisions = group
                    .Where(row => row.ReleaseVerified && IsHealthSha(row.GitCommitHash))
                    .Select(row => row.GitCommitHash!.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray(),
                observedUnverifiedRevisions = group
                    .Where(row => !row.ReleaseVerified && IsHealthSha(row.GitCommitHash))
                    .Select(row => row.GitCommitHash!.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray()
            })
            .Take(64)
            .ToArray();

        return new
        {
            ok = true,
            schemaVersion = 1,
            disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.OperationalMetadata,
            sourceAuthority = "RuntimeDiagnosticStore",
            recordsExist = rows.Count > 0,
            boundedRowsObserved = rows.Count,
            truncated,
            surfaces,
            privacy = new
            {
                userIdentifiersIncluded = false,
                routesIncluded = false,
                sourceFilesIncluded = false,
                stackTracesIncluded = false,
                correlationIdsIncluded = false,
                timestampsIncluded = false,
                rawMessagesIncluded = false,
                requestOrResponseBodiesIncluded = false,
                credentialMaterialIncluded = false
            }
        };
    }

    private static string SafeHealthSymbol(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? value
            : "unknown";

    private static bool IsHealthSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
