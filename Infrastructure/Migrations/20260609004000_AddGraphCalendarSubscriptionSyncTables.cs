using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260609004000_AddGraphCalendarSubscriptionSyncTables")]
    public partial class AddGraphCalendarSubscriptionSyncTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraphCalendarSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CalendarUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CalendarEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    GraphSubscriptionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Resource = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ChangeType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ClientState = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpirationUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRenewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastWebhookUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphCalendarSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentSyncLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkstationLeadId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClientProfileId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CalendarUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CalendarEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    GraphSubscriptionId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GraphEventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    DiagnosticJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentSyncLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_GraphSubscriptionId",
                table: "GraphCalendarSubscriptions",
                column: "GraphSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_AgentUserId_CalendarEmail",
                table: "GraphCalendarSubscriptions",
                columns: new[] { "AgentUserId", "CalendarEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_IsActive_ExpirationUtc",
                table: "GraphCalendarSubscriptions",
                columns: new[] { "IsActive", "ExpirationUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_AppointmentId",
                table: "AppointmentSyncLogs",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_WorkstationLeadId",
                table: "AppointmentSyncLogs",
                column: "WorkstationLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_GraphEventId",
                table: "AppointmentSyncLogs",
                column: "GraphEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_CreatedUtc",
                table: "AppointmentSyncLogs",
                column: "CreatedUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AppointmentSyncLogs");
            migrationBuilder.DropTable(name: "GraphCalendarSubscriptions");
        }
    }
}
