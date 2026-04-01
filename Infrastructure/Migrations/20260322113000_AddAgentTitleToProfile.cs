using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    // Manual migration to add agent Title field
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260322113000_AddAgentTitleToProfile")]
    public partial class AddAgentTitleToProfile : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // Table created with Title in earlier SQLite repair; no-op to avoid duplicate column.
            }
            else
            {
                migrationBuilder.AddColumn<string>(
                    name: "Title",
                    table: "AgentProfiles",
                    maxLength: 120,
                    nullable: true);
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "AgentProfiles");
        }
    }
}
