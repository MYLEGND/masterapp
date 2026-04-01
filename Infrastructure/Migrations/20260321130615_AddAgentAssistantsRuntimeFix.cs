using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAssistantsRuntimeFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentAssistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AssistantUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAssistants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_AssistantUserId",
                table: "AgentAssistants",
                column: "AssistantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId",
                table: "AgentAssistants",
                column: "ParentAgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId_Email",
                table: "AgentAssistants",
                columns: new[] { "ParentAgentUserId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAssistants");
        }
    }
}
