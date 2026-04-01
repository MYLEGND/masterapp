using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _20260209_AddFinanceToolStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ToolKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceToolStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceToolStates_ClientUserId_ToolKey",
                table: "FinanceToolStates",
                columns: new[] { "ClientUserId", "ToolKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceToolStates");
        }
    }
}
