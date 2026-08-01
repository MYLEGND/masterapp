using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingGroupsAndVerificationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerParticipantType",
                table: "MessageConversations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "MessageConversations",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "MessageConversations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_Purpose_CreatedByUserId_OwnerParticipantType",
                table: "MessageConversations",
                columns: new[] { "Purpose", "CreatedByUserId", "OwnerParticipantType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageConversations_Purpose_CreatedByUserId_OwnerParticipantType",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "OwnerParticipantType",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "MessageConversations");
        }
    }
}
