using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddFinanceToolStatesClean : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("DROP TABLE IF EXISTS \"FinanceToolStates\";");
            }
            else
            {
                migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[FinanceToolStates]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[FinanceToolStates];
END
");
            }

            migrationBuilder.CreateTable(
                name: "FinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JsonState = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceToolStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceToolStates_ClientProfileId_ToolId",
                table: "FinanceToolStates",
                columns: new[] { "ClientProfileId", "ToolId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceToolStates");
        }
    }
}
