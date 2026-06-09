using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260609002500_AddCanonicalAppointmentSyncFields")]
    public partial class AddCanonicalAppointmentSyncFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BookingProvider",
                table: "LeadAppointments",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientProfileId",
                table: "LeadAppointments",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncError",
                table: "LeadAppointments",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncStatus",
                table: "LeadAppointments",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncedUtc",
                table: "LeadAppointments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchConfidence",
                table: "LeadAppointments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RawProviderPayloadJson",
                table: "LeadAppointments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteLeadId",
                table: "LeadAppointments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_BookingProvider_CalendarEventId",
                table: "LeadAppointments",
                columns: new[] { "BookingProvider", "CalendarEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_ClientProfileId",
                table: "LeadAppointments",
                column: "ClientProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WebsiteLeadId",
                table: "LeadAppointments",
                column: "WebsiteLeadId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeadAppointments_BookingProvider_CalendarEventId",
                table: "LeadAppointments");

            migrationBuilder.DropIndex(
                name: "IX_LeadAppointments_ClientProfileId",
                table: "LeadAppointments");

            migrationBuilder.DropIndex(
                name: "IX_LeadAppointments_WebsiteLeadId",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingProvider",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "ClientProfileId",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "LastSyncError",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "LastSyncStatus",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "LastSyncedUtc",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "MatchConfidence",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "RawProviderPayloadJson",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "WebsiteLeadId",
                table: "LeadAppointments");
        }
    }
}
