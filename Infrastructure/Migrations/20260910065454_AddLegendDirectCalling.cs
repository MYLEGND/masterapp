using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendDirectCalling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendCallSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CallerType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CalleeUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CalleeType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CallerDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalleeDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CallerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CalleeName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Video = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Epoch = table.Column<int>(type: "int", nullable: false),
                    InvitationDispatchedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextPushUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PushAttempts = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCallSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendCallSignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientGroup = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCallSignals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCallSessions_CalleeUserId_CalleeType_ExpiresUtc",
                table: "LegendCallSessions",
                columns: new[] { "CalleeUserId", "CalleeType", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCallSessions_CallerUserId_CallerType_ExpiresUtc",
                table: "LegendCallSessions",
                columns: new[] { "CallerUserId", "CallerType", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCallSignals_CallId_CreatedUtc",
                table: "LegendCallSignals",
                columns: new[] { "CallId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCallSignals_ExpiresUtc",
                table: "LegendCallSignals",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendCallSignals_RecipientGroup_ExpiresUtc",
                table: "LegendCallSignals",
                columns: new[] { "RecipientGroup", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendCallSessions");

            migrationBuilder.DropTable(
                name: "LegendCallSignals");
        }
    }
}
