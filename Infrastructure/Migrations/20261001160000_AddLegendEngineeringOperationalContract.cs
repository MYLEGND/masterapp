using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20261001160000_AddLegendEngineeringOperationalContract")]
    public partial class AddLegendEngineeringOperationalContract : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendEngineeringOperationalContract",
                columns: table => new
                {
                    ContractKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ModelExecutionEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SharedDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    HeadGptDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    CodexDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    ReviewerDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringOperationalContract", x => x.ContractKey));

            migrationBuilder.CreateTable(
                name: "LegendEngineeringOperationalContractHistory",
                columns: table => new
                {
                    Revision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ModelExecutionEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SharedDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    HeadGptDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    CodexDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    ReviewerDirective = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringOperationalContractHistory", x => x.Revision));

            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringOperationalContractHistory_Version",
                table: "LegendEngineeringOperationalContractHistory",
                column: "Version",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LegendEngineeringOperationalContractHistory");
            migrationBuilder.DropTable(name: "LegendEngineeringOperationalContract");
        }
    }
}
