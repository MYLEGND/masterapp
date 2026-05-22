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
            migrationBuilder.CreateTable(
                name: "LeadAppointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkstationLeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    WebsiteLeadIntakeLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BookingSource = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CalendarEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CalendarEventWebLink = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MeetingUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastStatusChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BookedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NoShowUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RescheduledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                        onDelete: ReferentialAction.Cascade);
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
