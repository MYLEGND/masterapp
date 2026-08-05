using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountClosureExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClosureAttemptCount",
                table: "AccountLifecycleRecords",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosureLeaseExpiresUtc",
                table: "AccountLifecycleRecords",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosureLeaseId",
                table: "AccountLifecycleRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastClosureAttemptUtc",
                table: "AccountLifecycleRecords",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastClosureErrorCode",
                table: "AccountLifecycleRecords",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountLifecycleAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountLifecycleRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ResultCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountLifecycleAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountLifecycleRecords_State_ClosureLeaseExpiresUtc",
                table: "AccountLifecycleRecords",
                columns: new[] { "State", "ClosureLeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountLifecycleAuditEntries_AccountLifecycleRecordId_OccurredUtc",
                table: "AccountLifecycleAuditEntries",
                columns: new[] { "AccountLifecycleRecordId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountLifecycleAuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountLifecycleRecords_State_ClosureLeaseExpiresUtc",
                table: "AccountLifecycleRecords");

            migrationBuilder.DropColumn(
                name: "ClosureAttemptCount",
                table: "AccountLifecycleRecords");

            migrationBuilder.DropColumn(
                name: "ClosureLeaseExpiresUtc",
                table: "AccountLifecycleRecords");

            migrationBuilder.DropColumn(
                name: "ClosureLeaseId",
                table: "AccountLifecycleRecords");

            migrationBuilder.DropColumn(
                name: "LastClosureAttemptUtc",
                table: "AccountLifecycleRecords");

            migrationBuilder.DropColumn(
                name: "LastClosureErrorCode",
                table: "AccountLifecycleRecords");
        }
    }
}
