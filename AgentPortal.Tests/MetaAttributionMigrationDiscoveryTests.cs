using System;
using System.Linq;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MetaAttributionMigrationDiscoveryTests
{
    [Fact]
    public void ExistingAppliedMigrationIsRegisteredExactlyOnce()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=(local);Database=MetadataOnly;Integrated Security=true")
            .Options;
        using var db = new MasterAppDbContext(options);
        var migrations = db.Database.GetMigrations().ToArray();
        const string id = "20260516100000_AddMetaAttributionReconciliation";
        Assert.Equal(1, migrations.Count(value =>
            string.Equals(value, id, StringComparison.Ordinal)));
        Assert.Contains("20260516072716_AddWebsiteLeadSoftDelete", migrations);
        Assert.Contains("20260516100806_AddAgentProfileMetaFields", migrations);
    }
}
