using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20261001153000_AddRuntimeDiagnosticStructuralReproducer")]
    public partial class AddRuntimeDiagnosticStructuralReproducer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var largeTextType = ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                ? "nvarchar(max)"
                : "TEXT";
            migrationBuilder.AddColumn<string>(
                name: "StructuralReproducerJson",
                table: "RuntimeDiagnosticIncidents",
                type: largeTextType,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StructuralReproducerJson",
                table: "RuntimeDiagnosticIncidents");
        }
    }
}
