using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentPhoneToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // Ensure table exists in SQLite dev files that missed earlier migration.
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
                // Phone column already included in create-if-missing above; skip ALTER on SQLite.
            }
            else
            {
                migrationBuilder.AddColumn<string>(
                    name: "Phone",
                    table: "AgentProfiles",
                    maxLength: 64,
                    nullable: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Phone",
                table: "AgentProfiles");
        }
    }
}
