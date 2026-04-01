using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260324083000_AddAgentProfileDisplayOrder")]
    public partial class AddAgentProfileDisplayOrder : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // DisplayOrder already in repair create; skip duplicate add.
        }
        else
        {
            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "AgentProfiles",
                type: "INTEGER",
                nullable: true);
        }
        }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DisplayOrder",
            table: "AgentProfiles");
    }
}
