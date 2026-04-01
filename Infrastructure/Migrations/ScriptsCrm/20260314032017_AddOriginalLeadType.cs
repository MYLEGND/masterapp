using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    public partial class AddOriginalLeadType : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalLeadType",
                table: "ScriptLeadProfiles",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "MortgageProtection");

            // Backfill origin to current bucket for existing rows
            migrationBuilder.Sql("""
                UPDATE ScriptLeadProfiles
                SET OriginalLeadType = Bucket
                WHERE OriginalLeadType IS NULL
                   OR OriginalLeadType = ''
            """);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_OriginalLeadType",
                table: "ScriptLeadProfiles",
                column: "OriginalLeadType");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId_OriginalLeadType",
                table: "ScriptLeadProfiles",
                columns: new[] { "AgentUserId", "OriginalLeadType" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId_OriginalLeadType",
                table: "ScriptLeadProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ScriptLeadProfiles_OriginalLeadType",
                table: "ScriptLeadProfiles");

            migrationBuilder.DropColumn(
                name: "OriginalLeadType",
                table: "ScriptLeadProfiles");
        }
    }
}
