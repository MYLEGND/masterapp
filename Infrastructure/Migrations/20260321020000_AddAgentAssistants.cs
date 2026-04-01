using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddAgentAssistants : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentAssistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ParentAgentUserId = table.Column<string>(maxLength: 450, nullable: false),
                    AssistantUserId = table.Column<string>(maxLength: 450, nullable: true),
                    FirstName = table.Column<string>(maxLength: 100, nullable: false),
                    LastName = table.Column<string>(maxLength: 100, nullable: false),
                    Email = table.Column<string>(maxLength: 320, nullable: false),
                    IsActive = table.Column<bool>(nullable: false),
                    InvitedAt = table.Column<DateTime>(nullable: false),
                    CreatedUtc = table.Column<DateTime>(nullable: false)
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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentAssistants");
        }
    }
}
