using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFounderPromotedMessagingGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPromoted",
                table: "MessageConversations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromotionEndedUtc",
                table: "MessageConversations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromotionStartedUtc",
                table: "MessageConversations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_IsPromoted_PromotionStartedUtc",
                table: "MessageConversations",
                columns: new[] { "IsPromoted", "PromotionStartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageConversations_IsPromoted_PromotionStartedUtc",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "IsPromoted",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "PromotionEndedUtc",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "PromotionStartedUtc",
                table: "MessageConversations");
        }
    }
}
