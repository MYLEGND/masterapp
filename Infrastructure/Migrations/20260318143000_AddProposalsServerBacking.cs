using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260318143000_AddProposalsServerBacking")]
public partial class AddProposalsServerBacking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Proposals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LeadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                LeadName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                BucketsJson = table.Column<string>(nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Proposals", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_AgentUserId",
            table: "Proposals",
            column: "AgentUserId");

        migrationBuilder.CreateIndex(
            name: "IX_Proposals_AgentUserId_LeadId",
            table: "Proposals",
            columns: new[] { "AgentUserId", "LeadId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Proposals");
    }
}
