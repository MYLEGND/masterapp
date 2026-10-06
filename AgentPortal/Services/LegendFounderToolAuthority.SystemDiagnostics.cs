using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

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

    private object ReadConfigurationPresence(string capability)
    {
        if (_authorizationScopes is null)
            return new
            {
                ok = false,
                error = "configuration_presence_authority_unavailable",
                disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.ExistenceOnly
            };

        using var scope = _authorizationScopes.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
        bool Any(params string[] keys) => keys.Any(key => Present(configuration[key]));
        ConfigurationPresenceResult Result(string name, params (string Role, bool Present)[] inputs)
        {
            var missing = inputs.Where(input => !input.Present).Select(input => input.Role).ToArray();
            return new(
                name,
                missing.Length == 0,
                inputs.Count(input => input.Present),
                inputs.Length,
                missing,
                Readable: false,
                Shared.Diagnostics.LegendSiteToolDisclosureAuthority.ExistenceOnly,
                "server_configuration");
        }

        var results = new[]
        {
            Result("founder_identity",
                ("founder_oid", Any("FOUNDER_OID", "Founder:Oid"))),
            Result("master_database",
                ("masterapp_connection", Present(configuration.GetConnectionString("MasterAppDb")) ||
                    Any("ConnectionStrings:MasterAppDb"))),
            Result("github_remediation",
                ("enabled", configuration.GetValue<bool?>("FounderSoftwareRemediation:Enabled") == true),
                ("repository", Any("FounderSoftwareRemediation:RepositoryOwner")),
                ("repository_name", Any("FounderSoftwareRemediation:RepositoryName")),
                ("base_branch", Any("FounderSoftwareRemediation:BaseBranch")),
                ("github_app", Any("FounderSoftwareRemediation:GitHubAppId")),
                ("github_installation", Any("FounderSoftwareRemediation:GitHubInstallationId")),
                ("private_key_reference", Any("FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"))),
            Result("data_protection",
                ("key_store", Any("DataProtection:BlobUri")),
                ("key_protector", Any("DataProtection:KeyVaultKeyId"))),
            Result("website_editor_data_protection",
                ("key_store", Any("WebsiteEditorDataProtection:BlobUri", "DataProtection:BlobUri")),
                ("key_protector", Any("WebsiteEditorDataProtection:KeyVaultKeyId", "DataProtection:KeyVaultKeyId"))),
            Result("meta_ads",
                ("application_id", Any("MetaAds:AppId")),
                ("application_secret", Any("MetaAds:AppSecret"))),
            Result("google_ads",
                ("oauth_client_id", Any("GoogleAds:ClientId")),
                ("oauth_client_secret", Any("GoogleAds:ClientSecret")),
                ("developer_token", Any("GoogleAds:DeveloperToken")),
                ("redirect_uri", Any("GoogleAds:RedirectUri"))),
            Result("tiktok_ads",
                ("application_id", Any("TikTokAds:AppId")),
                ("application_secret", Any("TikTokAds:AppSecret")),
                ("advertiser_authorization_url", Any("TikTokAds:AdvertiserAuthorizationUrl")),
                ("redirect_uri", Any("TikTokAds:RedirectUri"))),
            Result("graph_provisioning",
                ("tenant", Any("GraphProvisioning:TenantId", "AzureAd:TenantId")),
                ("client", Any("GraphProvisioning:ClientId", "AzureAd:ClientId")),
                ("client_secret", Any("GraphProvisioning:ClientSecret", "AzureAd:ClientSecret"))),
            Result("azure_translator",
                ("endpoint", Any("AzureTranslator:Endpoint")),
                ("credential", Any("AzureTranslator:Key")),
                ("region", Any("AzureTranslator:Region"))),
            Result("square_server_payments",
                ("access_credential", Any("Square:AccessToken", "Square:Token", "Square:SecretAccessToken")),
                ("location", Any("Square:LocationId", "Square:PublicLocationId", "Square:WebPaymentsLocationId")))
        };

        if (string.Equals(capability, "all", StringComparison.Ordinal))
            return new
            {
                ok = true,
                schemaVersion = 1,
                disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.ExistenceOnly,
                valuesReadable = false,
                results
            };

        var selected = results.SingleOrDefault(result => string.Equals(result.Capability, capability, StringComparison.Ordinal));
        return selected is null
            ? new
            {
                ok = false,
                error = "configuration_capability_not_allowed",
                disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.ExistenceOnly
            }
            : new
            {
                ok = true,
                schemaVersion = 1,
                disclosureClass = Shared.Diagnostics.LegendSiteToolDisclosureAuthority.ExistenceOnly,
                valuesReadable = false,
                result = selected
            };
    }

    private sealed record ConfigurationPresenceResult(
        string Capability,
        bool Configured,
        int PresentInputs,
        int RequiredInputs,
        string[] MissingRoles,
        bool Readable,
        string DisclosureClass,
        string Source);

    private static string SafeHealthSymbol(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? value
            : "unknown";

    private static bool IsHealthSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
