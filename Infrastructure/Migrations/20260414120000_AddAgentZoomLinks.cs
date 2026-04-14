using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260414120000_AddAgentZoomLinks")]
public partial class AddAgentZoomLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentZoomLinks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentZoomLinks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentZoomLinks_AgentUserId",
            table: "AgentZoomLinks",
            column: "AgentUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AgentZoomLinks");
    }
}
