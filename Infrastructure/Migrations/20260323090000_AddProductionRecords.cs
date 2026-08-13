using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260323090000_AddProductionRecords")]
public partial class AddProductionRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlServer = (migrationBuilder.ActiveProvider ?? string.Empty).Contains(
            "SqlServer",
            StringComparison.OrdinalIgnoreCase);

        string GuidType() => isSqlServer ? "uniqueidentifier" : "TEXT";
        string DateType() => isSqlServer ? "datetime2" : "TEXT";
        string IntType() => isSqlServer ? "int" : "INTEGER";
        string StringType(int? maxLength = null) =>
            isSqlServer
                ? maxLength.HasValue
                    ? $"nvarchar({maxLength.Value})"
                    : "nvarchar(max)"
                : "TEXT";

        migrationBuilder.CreateTable(
            name: "ProductionRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: GuidType(), nullable: false),
                AgentUserId = table.Column<string>(type: StringType(450), maxLength: 450, nullable: false),
                Side = table.Column<int>(type: IntType(), nullable: false),
                Status = table.Column<int>(type: IntType(), nullable: false),
                Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                LeadId = table.Column<string>(type: StringType(128), maxLength: 128, nullable: true),
                ClientUserId = table.Column<string>(type: StringType(450), maxLength: 450, nullable: true),
                Notes = table.Column<string>(type: StringType(240), maxLength: 240, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: DateType(), nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: DateType(), nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductionRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProductionRecords_AgentUserId",
            table: "ProductionRecords",
            column: "AgentUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductionRecords_AgentUserId_Side",
            table: "ProductionRecords",
            columns: new[] { "AgentUserId", "Side" });

        migrationBuilder.CreateIndex(
            name: "IX_ProductionRecords_ClientUserId",
            table: "ProductionRecords",
            column: "ClientUserId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductionRecords_LeadId",
            table: "ProductionRecords",
            column: "LeadId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductionRecords_Status",
            table: "ProductionRecords",
            column: "Status");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProductionRecords");
    }
}
