using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Stage 1 Identity Hardening:
    /// 1. Drops IX_ClientProfiles_Email (raw email unique index).
    ///    NormalizedEmail is the enforced uniqueness guardrail going forward.
    /// 2. Replaces IX_AgentAssistants_AssistantUserId with a filtered version on SQL Server
    ///    so multiple rows may have NULL AssistantUserId while awaiting invite acceptance.
    /// </summary>
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260401000000_Stage1IdentityHardening")]
    public partial class Stage1IdentityHardening : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // -----------------------------------------------------------------------
            // 1. Remove raw Email unique index from ClientProfiles.
            //    NormalizedEmail (IX_ClientProfiles_NormalizedEmail, filtered) is the
            //    uniqueness guardrail. The raw Email field is still present and populated
            //    but no longer carries a DB-level uniqueness constraint.
            // -----------------------------------------------------------------------
            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles");

            // -----------------------------------------------------------------------
            // 2. Fix AssistantUserId unique index on SQL Server.
            //    The original index (no filter) only permits one NULL value, which blocks
            //    creating a second assistant whose invite is still pending.
            //    Replace with a filtered index so NULLs (pre-binding rows) are excluded.
            //    SQLite allows multiple NULLs in a unique index natively; no change needed.
            // -----------------------------------------------------------------------
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.DropIndex(
                    name: "IX_AgentAssistants_AssistantUserId",
                    table: "AgentAssistants");

                migrationBuilder.CreateIndex(
                    name: "IX_AgentAssistants_AssistantUserId",
                    table: "AgentAssistants",
                    column: "AssistantUserId",
                    unique: true,
                    filter: "[AssistantUserId] IS NOT NULL");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore raw Email unique index on ClientProfiles.
            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles",
                column: "Email",
                unique: true);

            // Restore unfiltered AssistantUserId unique index on SQL Server.
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.DropIndex(
                    name: "IX_AgentAssistants_AssistantUserId",
                    table: "AgentAssistants");

                migrationBuilder.CreateIndex(
                    name: "IX_AgentAssistants_AssistantUserId",
                    table: "AgentAssistants",
                    column: "AssistantUserId",
                    unique: true);
            }
        }
    }
}
