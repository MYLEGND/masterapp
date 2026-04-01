using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260311123000_AddAgentUserIdToScriptLeadProfiles")]
    public partial class AddAgentUserIdToScriptLeadProfiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentUserId",
                table: "ScriptLeadProfiles",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId",
                table: "ScriptLeadProfiles",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId_Phone",
                table: "ScriptLeadProfiles",
                columns: new[] { "AgentUserId", "Phone" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId",
                table: "ScriptLeadProfiles");

            migrationBuilder.DropIndex(
                name: "IX_ScriptLeadProfiles_AgentUserId_Phone",
                table: "ScriptLeadProfiles");

            migrationBuilder.DropColumn(
                name: "AgentUserId",
                table: "ScriptLeadProfiles");
        }
    }
}
