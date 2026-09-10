using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingReadReceiptPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SendReadReceipts",
                table: "MobileProfileSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SharedReadThroughUtc",
                table: "MessageConversationParticipants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SuppressReadReceipts",
                table: "MessageConversationParticipants",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SendReadReceipts",
                table: "MobileProfileSettings");

            migrationBuilder.DropColumn(
                name: "SharedReadThroughUtc",
                table: "MessageConversationParticipants");

            migrationBuilder.DropColumn(
                name: "SuppressReadReceipts",
                table: "MessageConversationParticipants");
        }
    }
}
