using System;
using Infrastructure.Data;
using Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
namespace AgentPortal.Tests;
public sealed class RuntimeDiagnosticsMigrationTests
{
    [Fact]
    public void MigrationIsOneNullableColumnAndApplicationRollbackDoesNotRequireDestructiveDown()
    {
        var migration = new RuntimeDiagnosticPerformanceEvidence();
        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("RuntimeDiagnosticIncidents", operation.Table);
        Assert.Equal("PerformanceEvidenceJson", operation.Name);
        Assert.True(operation.IsNullable); Assert.Equal(4096, operation.MaxLength);
        Assert.Null(operation.DefaultValue); Assert.Null(operation.DefaultValueSql);
        Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations)); // destructive, not application rollback
    }
    [Fact]
    public void SqlServerScriptIsAdditiveAndSnapshotMatchesCurrentModelWithoutConnecting()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=fixture.invalid;Database=fixture;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20261026223148_ReceptionistBrowserCreation", "20261030033900_RuntimeDiagnosticPerformanceEvidence");
        Assert.Contains("ADD [PerformanceEvidenceJson] nvarchar(max) NULL", script);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
    }
}
