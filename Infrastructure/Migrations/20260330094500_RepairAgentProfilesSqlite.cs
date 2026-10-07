using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// SQLite repair: ensure AgentProfiles exists before later ALTERs.
    /// Safe no-op on SQL Server.
    /// </summary>
    [Migration("20260330094500_RepairAgentProfilesSqlite")]
    public partial class RepairAgentProfilesSqlite : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // Create table if missing (SQLite only). Minimal columns to satisfy later ALTERs.
                migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""AgentProfiles"" (
    ""Id"" TEXT NOT NULL CONSTRAINT PK_AgentProfiles PRIMARY KEY,
    ""AgentUserId"" TEXT NOT NULL,
    ""AgentUpn"" TEXT,
    ""FullName"" TEXT,
    ""Title"" TEXT,
    ""Npn"" TEXT,
    ""Phone"" TEXT,
    ""DisplayOrder"" INTEGER,
    ""CreatedUtc"" TEXT NOT NULL DEFAULT (datetime('now')),
    ""UpdatedUtc"" TEXT NOT NULL DEFAULT (datetime('now'))
);");

                // Add Phone column if still missing (SQLite >=3.35 supports IF NOT EXISTS).
                migrationBuilder.Sql(@"ALTER TABLE ""AgentProfiles"" ADD COLUMN IF NOT EXISTS ""Phone"" TEXT NULL;");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: do not drop repair artifacts.
        }
    }
}
