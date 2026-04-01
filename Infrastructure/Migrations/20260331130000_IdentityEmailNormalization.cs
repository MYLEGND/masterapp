using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260331130000_IdentityEmailNormalization")]
    public partial class IdentityEmailNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "OnboardingInvites",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "AgentProfiles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "AgentAssistants",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            // Backfill from source email columns
            migrationBuilder.Sql("UPDATE AgentProfiles SET NormalizedEmail = LOWER(LTRIM(RTRIM(AgentUpn))) WHERE NormalizedEmail IS NULL AND AgentUpn IS NOT NULL AND LTRIM(RTRIM(AgentUpn)) <> '';");
            migrationBuilder.Sql("UPDATE ClientProfiles SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';");
            migrationBuilder.Sql("UPDATE AgentAssistants SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';");
            migrationBuilder.Sql("UPDATE OnboardingInvites SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email))) WHERE NormalizedEmail IS NULL AND Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> '';");

            // Guard: fail migration if duplicates remain before adding unique indexes (SQL Server only)
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
IF EXISTS (SELECT NormalizedEmail FROM AgentProfiles WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*) > 1)
    THROW 50001, 'Duplicate NormalizedEmail found in AgentProfiles. Resolve before applying unique index.', 1;
IF EXISTS (SELECT NormalizedEmail FROM ClientProfiles WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*) > 1)
    THROW 50002, 'Duplicate NormalizedEmail found in ClientProfiles. Resolve before applying unique index.', 1;
IF EXISTS (SELECT NormalizedEmail FROM AgentAssistants WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*) > 1)
    THROW 50003, 'Duplicate NormalizedEmail found in AgentAssistants. Resolve before applying unique index.', 1;
IF EXISTS (SELECT NormalizedEmail FROM OnboardingInvites WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*) > 1)
    THROW 50004, 'Duplicate NormalizedEmail found in OnboardingInvites. Resolve before applying unique index.', 1;
");
            }

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingInvites_NormalizedEmail",
                table: "OnboardingInvites",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_NormalizedEmail",
                table: "AgentProfiles",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_NormalizedEmail",
                table: "AgentAssistants",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            // IX_ClientProfiles_NormalizedEmail already created by 20260315010000_AddNormalizedEmailUnique.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OnboardingInvites_NormalizedEmail",
                table: "OnboardingInvites");

            migrationBuilder.DropIndex(
                name: "IX_AgentProfiles_NormalizedEmail",
                table: "AgentProfiles");

            migrationBuilder.DropIndex(
                name: "IX_AgentAssistants_NormalizedEmail",
                table: "AgentAssistants");

            // IX_ClientProfiles_NormalizedEmail owned by 20260315010000_AddNormalizedEmailUnique; not touched here.

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "OnboardingInvites");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "AgentAssistants");
        }
    }
}
