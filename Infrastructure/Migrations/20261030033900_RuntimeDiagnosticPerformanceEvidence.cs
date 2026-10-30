using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;

#nullable disable
namespace Infrastructure.Migrations;
public partial class RuntimeDiagnosticPerformanceEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "PerformanceEvidenceJson", table: "RuntimeDiagnosticIncidents", type: "nvarchar(max)",
        maxLength: 4096, nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "PerformanceEvidenceJson", table: "RuntimeDiagnosticIncidents");
}
