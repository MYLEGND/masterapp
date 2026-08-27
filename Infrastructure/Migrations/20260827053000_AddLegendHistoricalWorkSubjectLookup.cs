using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Gives the canonical historical scheduler a bounded subject-identity lookup
/// for its anti-join.  This changes no curriculum, evidence, work state, or
/// evaluator authority; it only prevents an empty seed-page probe from
/// scanning the complete durable ledger past its lease.
/// </summary>
[DbContext(typeof(MasterAppDbContext))]
[Migration("20260827053000_AddLegendHistoricalWorkSubjectLookup")]
public sealed class AddLegendHistoricalWorkSubjectLookup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_LegendHistoricalReevaluationWorkItems_SubjectLookup",
            table: "LegendHistoricalReevaluationWorkItems",
            columns: new[] { "EvaluatorVersion", "Phase", "WorkKind", "SubjectId", "SubjectScope" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_LegendHistoricalReevaluationWorkItems_SubjectLookup",
            table: "LegendHistoricalReevaluationWorkItems");
    }
}
