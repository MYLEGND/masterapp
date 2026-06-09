using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [Migration("20260403090000_AddClientFinancialPlan")]
    public partial class AddClientFinancialPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientFinancialPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JsonData = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientFinancialPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientFinancialPlans_ClientProfiles_ClientId",
                        column: x => x.ClientId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                migrationBuilder.CreateIndex(
                    name: "IX_ClientFinancialPlans_ClientId",
                    table: "ClientFinancialPlans",
                    column: "ClientId",
                    unique: true,
                    filter: "[IsDeleted] = 0");
            }
            else
            {
                migrationBuilder.CreateIndex(
                    name: "IX_ClientFinancialPlans_ClientId_IsDeleted",
                    table: "ClientFinancialPlans",
                    columns: new[] { "ClientId", "IsDeleted" },
                    unique: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientFinancialPlans");
        }
    }
}
