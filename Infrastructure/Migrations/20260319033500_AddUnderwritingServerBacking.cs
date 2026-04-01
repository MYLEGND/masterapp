using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260319033500_AddUnderwritingServerBacking")]
public partial class AddUnderwritingServerBacking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDraft",
            table: "Proposals",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "LeadKey",
            table: "Proposals",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PageTitle",
            table: "Proposals",
            maxLength: 240,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "QueueKey",
            table: "Proposals",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ScopeKey",
            table: "Proposals",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "UnderwritingRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LeadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                LeadName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PayloadJson = table.Column<string>(nullable: false),
                ProductCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                QueueKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                ScopeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                PageTitle = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                IsDraft = table.Column<bool>(type: "bit", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UnderwritingRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UnderwritingRecords_AgentUserId",
            table: "UnderwritingRecords",
            column: "AgentUserId");

        migrationBuilder.CreateIndex(
            name: "IX_UnderwritingRecords_AgentUserId_LeadId",
            table: "UnderwritingRecords",
            columns: new[] { "AgentUserId", "LeadId" });

        migrationBuilder.CreateIndex(
            name: "IX_UnderwritingRecords_ProductCode",
            table: "UnderwritingRecords",
            column: "ProductCode");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UnderwritingRecords");

        migrationBuilder.DropColumn(
            name: "IsDraft",
            table: "Proposals");

        migrationBuilder.DropColumn(
            name: "LeadKey",
            table: "Proposals");

        migrationBuilder.DropColumn(
            name: "PageTitle",
            table: "Proposals");

        migrationBuilder.DropColumn(
            name: "QueueKey",
            table: "Proposals");

        migrationBuilder.DropColumn(
            name: "ScopeKey",
            table: "Proposals");
    }
}
