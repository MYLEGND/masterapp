using System;
using System.Linq;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MigrationDiscoveryRegressionTests
{
    [Fact]
    public void HistoricalAndAdvertisingMigrationsAreRegisteredInTheExactEfAssembly()
    {
        // GetMigrations inspects migration metadata without opening SQL.
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=localhost;Database=MigrationMetadataOnly;User ID=unused;Password=unused;TrustServerCertificate=True")
            .Options;
        using var db = new MasterAppDbContext(options);
        var registered = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);

        Assert.Contains("20260321020000_AddAgentAssistants", registered);
        Assert.Contains("20260329093000_ExecutionMvp", registered);
        Assert.Contains("20260330094500_RepairAgentProfilesSqlite", registered);
        Assert.Contains("20260927053000_AddAdvertisingActionAuthorizations", registered);
        Assert.Contains("20260213015112_InitialBaseline", registered);
        Assert.Contains("20260217173126_20260217_ModelSync", registered);
        Assert.Contains("20261007134500_AddFounderAssistantRules", registered);

        // This database-only audited migration is intentionally not fabricated
        // as executable SQL. The read-only probe validates its exact provenance.
        Assert.DoesNotContain("20260213015339_FinanceToolStates_ByClientProfile", registered);
    }
}
