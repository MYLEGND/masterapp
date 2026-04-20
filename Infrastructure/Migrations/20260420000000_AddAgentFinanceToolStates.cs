using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddAgentFinanceToolStates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentFinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JsonState = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentFinanceToolStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentFinanceToolStates_AgentUserId_ToolId",
                table: "AgentFinanceToolStates",
                columns: new[] { "AgentUserId", "ToolId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentFinanceToolStates");
        }
    }
}
