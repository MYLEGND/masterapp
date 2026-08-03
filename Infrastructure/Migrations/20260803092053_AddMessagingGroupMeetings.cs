using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingGroupMeetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostParticipantType",
                table: "MessageConversations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostUserId",
                table: "MessageConversations",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingCustomDescription",
                table: "MessageConversations",
                type: "nvarchar(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingFrequency",
                table: "MessageConversations",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingLinkLabel",
                table: "MessageConversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingLinkUrl",
                table: "MessageConversations",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingLocalTime",
                table: "MessageConversations",
                type: "nvarchar(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MeetingStartsUtc",
                table: "MessageConversations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingTimeZoneId",
                table: "MessageConversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingWeekdays",
                table: "MessageConversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostParticipantType",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "HostUserId",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingCustomDescription",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingFrequency",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingLinkLabel",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingLinkUrl",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingLocalTime",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingStartsUtc",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingTimeZoneId",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "MeetingWeekdays",
                table: "MessageConversations");
        }
    }
}
