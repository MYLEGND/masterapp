using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMobileProfileAndSocialFollowSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RespondedUtc",
                table: "SocialFollows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SocialFollows",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                // Existing relationship rows predate requests and are already
                // established follows, so migrate them as accepted.
                defaultValue: "Accepted");

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                table: "MobileProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
                table: "SocialFollows",
                columns: new[] { "FollowedUserId", "FollowedParticipantType", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "RespondedUtc",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                table: "MobileProfileSettings");
        }
    }
}
