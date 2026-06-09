using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAwarePublicBookingConfirmationSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlite = migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var string80Type = isSqlite ? "TEXT" : "nvarchar(80)";
            var string200Type = isSqlite ? "TEXT" : "nvarchar(200)";
            var string320Type = isSqlite ? "TEXT" : "nvarchar(320)";
            var string450Type = isSqlite ? "TEXT" : "nvarchar(450)";
            var string2048Type = isSqlite ? "TEXT" : "nvarchar(2048)";
            var guidType = isSqlite ? "TEXT" : "uniqueidentifier";
            var boolType = isSqlite ? "INTEGER" : "bit";

            migrationBuilder.AddColumn<string>(
                name: "BookingAgentSlug",
                table: "LeadAppointments",
                type: string200Type,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingAgentUserId",
                table: "LeadAppointments",
                type: string450Type,
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingCalendarEmail",
                table: "LeadAppointments",
                type: string320Type,
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingCalendarUserId",
                table: "LeadAppointments",
                type: string450Type,
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingConfigurationSource",
                table: "LeadAppointments",
                type: string80Type,
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingPageIdOrMailbox",
                table: "LeadAppointments",
                type: string320Type,
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BookingTrackingProfileId",
                table: "LeadAppointments",
                type: guidType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationSource",
                table: "LeadAppointments",
                type: string80Type,
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedBookingSource",
                table: "LeadAppointments",
                type: string80Type,
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "BookingEnabled",
                table: "AgentProfiles",
                type: boolType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingPageIdOrMailbox",
                table: "AgentProfiles",
                type: string320Type,
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarEmail",
                table: "AgentProfiles",
                type: string320Type,
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarUserId",
                table: "AgentProfiles",
                type: string450Type,
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FallbackBookingUrl",
                table: "AgentProfiles",
                type: string2048Type,
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MicrosoftBookingsEmbedUrl",
                table: "AgentProfiles",
                type: string2048Type,
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PreferModalOnMobile",
                table: "AgentProfiles",
                type: boolType,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BookingAgentSlug",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingAgentUserId",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingCalendarEmail",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingCalendarUserId",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingConfigurationSource",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingPageIdOrMailbox",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingTrackingProfileId",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "ConfirmationSource",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "RequestedBookingSource",
                table: "LeadAppointments");

            migrationBuilder.DropColumn(
                name: "BookingEnabled",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "BookingPageIdOrMailbox",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "CalendarEmail",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "CalendarUserId",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "FallbackBookingUrl",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "MicrosoftBookingsEmbedUrl",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "PreferModalOnMobile",
                table: "AgentProfiles");
        }
    }
}
