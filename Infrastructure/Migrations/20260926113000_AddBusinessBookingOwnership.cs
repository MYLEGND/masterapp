using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260926113000_AddBusinessBookingOwnership")]
public partial class AddBusinessBookingOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var guidType = sqlite ? "TEXT" : "uniqueidentifier";

        migrationBuilder.AddColumn<Guid>(
            name: "CommerceBusinessId",
            table: "LeadAppointments",
            type: guidType,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CommerceBusinessId",
            table: "GraphCalendarSubscriptions",
            type: guidType,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "CommerceBusinessId",
            table: "AppointmentSyncLogs",
            type: guidType,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_LeadAppointments_CommerceBusinessId_Status_ScheduledStartUtc",
            table: "LeadAppointments",
            columns: new[] { "CommerceBusinessId", "Status", "ScheduledStartUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_GraphCalendarSubscriptions_CommerceBusinessId_CalendarEmail",
            table: "GraphCalendarSubscriptions",
            columns: new[] { "CommerceBusinessId", "CalendarEmail" });

        migrationBuilder.CreateIndex(
            name: "IX_AppointmentSyncLogs_CommerceBusinessId",
            table: "AppointmentSyncLogs",
            column: "CommerceBusinessId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_LeadAppointments_CommerceBusinessId_Status_ScheduledStartUtc", table: "LeadAppointments");
        migrationBuilder.DropIndex(name: "IX_GraphCalendarSubscriptions_CommerceBusinessId_CalendarEmail", table: "GraphCalendarSubscriptions");
        migrationBuilder.DropIndex(name: "IX_AppointmentSyncLogs_CommerceBusinessId", table: "AppointmentSyncLogs");

        migrationBuilder.DropColumn(name: "CommerceBusinessId", table: "LeadAppointments");
        migrationBuilder.DropColumn(name: "CommerceBusinessId", table: "GraphCalendarSubscriptions");
        migrationBuilder.DropColumn(name: "CommerceBusinessId", table: "AppointmentSyncLogs");
    }
}
