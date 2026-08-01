using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingInboxControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenUtc",
                table: "MessageConversationParticipants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedUtc",
                table: "MessageConversationParticipants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_UserId_ParticipantType_HiddenUtc",
                table: "MessageConversationParticipants",
                columns: new[] { "UserId", "ParticipantType", "HiddenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_UserId_ParticipantType_PinnedUtc",
                table: "MessageConversationParticipants",
                columns: new[] { "UserId", "ParticipantType", "PinnedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageConversationParticipants_UserId_ParticipantType_HiddenUtc",
                table: "MessageConversationParticipants");

            migrationBuilder.DropIndex(
                name: "IX_MessageConversationParticipants_UserId_ParticipantType_PinnedUtc",
                table: "MessageConversationParticipants");

            migrationBuilder.DropColumn(
                name: "HiddenUtc",
                table: "MessageConversationParticipants");

            migrationBuilder.DropColumn(
                name: "PinnedUtc",
                table: "MessageConversationParticipants");
        }
    }
}
