using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProfileAndHouseholdHardening : Migration
    {
        /// <inheritdoc />
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "ClientProfiles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "AgentUpn",
                table: "AgentClients",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles",
                column: "Email",
                unique: true);

            // Clean existing duplicates so ONE-OWNER unique index can be applied safely
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
;WITH d AS (
    SELECT *,
           rn = ROW_NUMBER() OVER (
                PARTITION BY ClientUserId
                ORDER BY
                    CASE WHEN CreatedUtc IS NULL THEN 1 ELSE 0 END,
                    CreatedUtc ASC
           )
    FROM dbo.AgentClients
)
DELETE FROM d
WHERE rn > 1;
");
            }
            else if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
DELETE FROM AgentClients
WHERE rowid IN (
    SELECT rowid
    FROM (
        SELECT rowid,
               ROW_NUMBER() OVER (
                   PARTITION BY ClientUserId
                   ORDER BY
                       CASE WHEN CreatedUtc IS NULL THEN 1 ELSE 0 END,
                       CreatedUtc ASC,
                       rowid ASC
               ) AS rn
        FROM AgentClients
    ) x
    WHERE x.rn > 1
);
");
            }

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients",
                column: "ClientUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles");

            migrationBuilder.DropIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320);

            migrationBuilder.AlterColumn<string>(
                name: "AgentUpn",
                table: "AgentClients",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320);

            migrationBuilder.CreateTable(
                name: "FinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    ToolKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
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
    }
}
