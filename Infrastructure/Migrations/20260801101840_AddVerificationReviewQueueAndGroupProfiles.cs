using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationReviewQueueAndGroupProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "GroupImageContent",
                table: "MessageConversations",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupImageContentType",
                table: "MessageConversations",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerificationReviewRequestId",
                table: "InternalMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "ClientProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "AgentProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "VerificationReviewRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RequesterParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationReviewRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_Purpose",
                table: "MessageConversations",
                column: "Purpose",
                unique: true,
                filter: "[Purpose] = 'VerificationReview'");

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_VerificationReviewRequestId",
                table: "InternalMessages",
                column: "VerificationReviewRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType",
                table: "VerificationReviewRequests",
                columns: new[] { "RequesterUserId", "RequesterParticipantType" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_Status",
                table: "VerificationReviewRequests",
                columns: new[] { "RequesterUserId", "RequesterParticipantType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationReviewRequests_ReviewConversationId_RequestedUtc",
                table: "VerificationReviewRequests",
                columns: new[] { "ReviewConversationId", "RequestedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerificationReviewRequests");

            migrationBuilder.DropIndex(
                name: "IX_MessageConversations_Purpose",
                table: "MessageConversations");

            migrationBuilder.DropIndex(
                name: "IX_InternalMessages_VerificationReviewRequestId",
                table: "InternalMessages");

            migrationBuilder.DropColumn(
                name: "GroupImageContent",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "GroupImageContentType",
                table: "MessageConversations");

            migrationBuilder.DropColumn(
                name: "VerificationReviewRequestId",
                table: "InternalMessages");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "AgentProfiles");
        }
    }
}
