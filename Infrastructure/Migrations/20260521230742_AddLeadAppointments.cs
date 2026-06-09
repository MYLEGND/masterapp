using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlite = migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var guidType = isSqlite ? "TEXT" : "uniqueidentifier";
            var workstationLeadIdType = isSqlite ? "TEXT" : "nvarchar(64)";
            var ownerAgentUserIdType = isSqlite ? "TEXT" : "nvarchar(450)";
            var statusType = isSqlite ? "TEXT" : "nvarchar(32)";
            var bookingSourceType = isSqlite ? "TEXT" : "nvarchar(80)";
            var calendarEventIdType = isSqlite ? "TEXT" : "nvarchar(256)";
            var urlType = isSqlite ? "TEXT" : "nvarchar(2048)";
            var dateTimeType = isSqlite ? "TEXT" : "datetime2";
            var workstationLeadDeleteBehavior = isSqlite ? ReferentialAction.Cascade : ReferentialAction.NoAction;

            migrationBuilder.CreateTable(
                name: "LeadAppointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    WorkstationLeadId = table.Column<string>(type: workstationLeadIdType, maxLength: 64, nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: ownerAgentUserIdType, maxLength: 450, nullable: false),
                    WebsiteLeadIntakeLinkId = table.Column<Guid>(type: guidType, nullable: true),
                    Status = table.Column<string>(type: statusType, maxLength: 32, nullable: false),
                    BookingSource = table.Column<string>(type: bookingSourceType, maxLength: 80, nullable: false),
                    CalendarEventId = table.Column<string>(type: calendarEventIdType, maxLength: 256, nullable: true),
                    CalendarEventWebLink = table.Column<string>(type: urlType, maxLength: 2048, nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ScheduledEndUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    MeetingUrl = table.Column<string>(type: urlType, maxLength: 2048, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: dateTimeType, nullable: false),
                    LastStatusChangedUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    BookedUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    NoShowUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: dateTimeType, nullable: true),
                    RescheduledUtc = table.Column<DateTime>(type: dateTimeType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadAppointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadAppointments_WebsiteLeadIntakeLinks_WebsiteLeadIntakeLinkId",
                        column: x => x.WebsiteLeadIntakeLinkId,
                        principalTable: "WebsiteLeadIntakeLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LeadAppointments_WorkstationLeadProfiles_WorkstationLeadId",
                        column: x => x.WorkstationLeadId,
                        principalTable: "WorkstationLeadProfiles",
                        principalColumn: "LeadId",
                        onDelete: workstationLeadDeleteBehavior);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_CalendarEventId",
                table: "LeadAppointments",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_OwnerAgentUserId_Status_ScheduledStartUtc",
                table: "LeadAppointments",
                columns: new[] { "OwnerAgentUserId", "Status", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WebsiteLeadIntakeLinkId",
                table: "LeadAppointments",
                column: "WebsiteLeadIntakeLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId",
                table: "LeadAppointments",
                column: "WorkstationLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId_ScheduledStartUtc",
                table: "LeadAppointments",
                columns: new[] { "WorkstationLeadId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId_UpdatedUtc",
                table: "LeadAppointments",
                columns: new[] { "WorkstationLeadId", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadAppointments");
        }
    }
}
