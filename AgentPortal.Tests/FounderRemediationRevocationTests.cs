using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Azure.Core;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderRemediationRevocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackedAuthorityCannotHideRevocationCommittedByAnotherRequest(bool publish)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using (var setup = new MasterAppDbContext(options))
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TABLE FounderSoftwareRemediationAuthorityStates (
                    Id TEXT PRIMARY KEY, ScopeKey TEXT NOT NULL UNIQUE, IsRevoked INTEGER NOT NULL,
                    RevokedUtc TEXT NULL, RevokedByUserId TEXT NULL, LastVerifiedUtc TEXT NULL,
                    ProtectedProductionBranchVerified INTEGER NOT NULL, SecurityCiVerified INTEGER NOT NULL,
                    RepairPreparationVerified INTEGER NOT NULL, LastVerificationCode TEXT NULL,
                    LastVerificationDetail TEXT NULL, UpdatedUtc TEXT NOT NULL, RowVersion BLOB NOT NULL);
                """);
            setup.FounderSoftwareRemediationAuthorityStates.Add(new FounderSoftwareRemediationAuthorityState
            {
                ScopeKey = "Global", IsRevoked = false, RepairPreparationVerified = true,
                ProtectedProductionBranchVerified = true, SecurityCiVerified = true
            });
            await setup.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FounderSoftwareRemediation:Enabled"] = "true",
            ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
            ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
            ["FounderSoftwareRemediation:BaseBranch"] = "production",
            ["FounderSoftwareRemediation:GitHubAppId"] = "1",
            ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
            ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://fixture.vault.azure.net/secrets/fixture",
            ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
        }).Build();
        var http = new DenyHttpFactory(); var credential = new DenyCredential();
        await using var requestDb = new MasterAppDbContext(options);
        var service = new FounderSoftwareRemediationService(http, configuration,
            NullLogger<FounderSoftwareRemediationService>.Instance, credential, requestDb);
        _ = await service.GetStatusAsync(CancellationToken.None);
        var tracked = Assert.Single(requestDb.ChangeTracker.Entries<FounderSoftwareRemediationAuthorityState>()).Entity;
        Assert.False(tracked.IsRevoked);

        await using (var revocationDb = new MasterAppDbContext(options))
        {
            var revoker = new FounderSoftwareRemediationService(http, configuration,
                NullLogger<FounderSoftwareRemediationService>.Instance, credential, revocationDb);
            await revoker.RevokeAsync("00466bf4-71ca-42c3-8877-c3a83986e786", CancellationToken.None);
        }
        // The old context genuinely retains stale state. The guard must query
        // committed authority instead of reusing this tracked instance.
        Assert.False(tracked.IsRevoked);
        Assert.True(await requestDb.FounderSoftwareRemediationAuthorityStates.AsNoTracking().Select(row => row.IsRevoked).SingleAsync());
        var result = publish
            ? await service.ReleaseApprovedAsync(123, new string('a', 40), CancellationToken.None)
            : await service.PrepareAsync("founder", new FounderSoftwareRepairProposal(
                new string('a', 40), "Bounded repair", "Reviewed source repair",
                new[] { new FounderSoftwareRepairChange("AgentPortal/Controllers/HomeController.cs", "namespace Fixture;") }),
                CancellationToken.None);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("software_remediation_not_configured", json.RootElement.GetProperty("error").GetString());
        Assert.Contains("revoked", json.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, http.Calls);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(requestDb.ChangeTracker.Entries<FounderSoftwareRepairBatch>());
        Assert.False(requestDb.ChangeTracker.HasChanges());
    }

    private sealed class DenyHttpFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("Revoked authority must not create a network client.");
        }
    }

    private sealed class DenyCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Revoked authority must not request a credential.");
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<AccessToken>(new InvalidOperationException("Revoked authority must not request a credential."));
        }
    }
}
