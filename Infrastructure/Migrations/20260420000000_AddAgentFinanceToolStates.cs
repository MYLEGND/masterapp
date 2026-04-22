using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260420000000_AddAgentFinanceToolStates")]
    public partial class AddAgentFinanceToolStates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
CREATE TABLE AgentFinanceToolStates (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_AgentFinanceToolStates PRIMARY KEY,
    AgentUserId nvarchar(450) NOT NULL,
    ToolId nvarchar(100) NOT NULL,
    JsonState nvarchar(max) NOT NULL,
    CreatedUtc datetime2 NOT NULL,
    UpdatedUtc datetime2 NOT NULL
);
CREATE UNIQUE INDEX IX_AgentFinanceToolStates_AgentUserId_ToolId
    ON AgentFinanceToolStates (AgentUserId, ToolId);
""");
            }
            else
            {
                migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "AgentFinanceToolStates" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentFinanceToolStates" PRIMARY KEY,
    "AgentUserId" TEXT NOT NULL,
    "ToolId" TEXT NOT NULL,
    "JsonState" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "UpdatedUtc" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentFinanceToolStates_AgentUserId_ToolId"
    ON "AgentFinanceToolStates" ("AgentUserId", "ToolId");
""");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentFinanceToolStates");
        }
    }
}
