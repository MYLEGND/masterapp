using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Keeps relationship-first feed candidate loading index-backed in each direction.
/// Mutual relationships remain derived from accepted directed follows; no duplicate
/// relationship state is persisted.
/// </summary>
[DbContext(typeof(MasterAppDbContext))]
[Migration("20260803013000_AddSocialFollowGraphQueryIndexes")]
public partial class AddSocialFollowGraphQueryIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType",
            table: "SocialFollows");

        migrationBuilder.DropIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
            table: "SocialFollows");

        migrationBuilder.CreateIndex(
            name: "IX_SocialFollows_FollowerUserId_FollowerParticipantType_Status_CreatedUtc",
            table: "SocialFollows",
            columns: new[] { "FollowerUserId", "FollowerParticipantType", "Status", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status_CreatedUtc",
            table: "SocialFollows",
            columns: new[] { "FollowedUserId", "FollowedParticipantType", "Status", "CreatedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SocialFollows_FollowerUserId_FollowerParticipantType_Status_CreatedUtc",
            table: "SocialFollows");

        migrationBuilder.DropIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status_CreatedUtc",
            table: "SocialFollows");

        migrationBuilder.CreateIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType",
            table: "SocialFollows",
            columns: new[] { "FollowedUserId", "FollowedParticipantType" });

        migrationBuilder.CreateIndex(
            name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType_Status",
            table: "SocialFollows",
            columns: new[] { "FollowedUserId", "FollowedParticipantType", "Status" });
    }
}
