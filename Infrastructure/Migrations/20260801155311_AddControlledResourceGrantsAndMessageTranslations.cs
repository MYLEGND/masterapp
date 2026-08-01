using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddControlledResourceGrantsAndMessageTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType",
                table: "VerificationReviewRequests");

            migrationBuilder.DropIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_Status",
                table: "VerificationReviewRequests");

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                table: "VerificationReviewRequests",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "VerificationBadge");

            migrationBuilder.AddColumn<string>(
                name: "PreferredCommunicationLanguage",
                table: "MobileProfileSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "InternalMessages",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ControlledResourceGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlledResourceGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InternalMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TranslatedText = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageTranslations_InternalMessages_InternalMessageId",
                        column: x => x.InternalMessageId,
                        principalTable: "InternalMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_ResourceType",
                table: "VerificationReviewRequests",
                columns: new[] { "RequesterUserId", "RequesterParticipantType", "ResourceType" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_ResourceType_Status",
                table: "VerificationReviewRequests",
                columns: new[] { "RequesterUserId", "RequesterParticipantType", "ResourceType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlledResourceGrants_ResourceType_IsActive",
                table: "ControlledResourceGrants",
                columns: new[] { "ResourceType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlledResourceGrants_UserId_ParticipantType_ResourceType",
                table: "ControlledResourceGrants",
                columns: new[] { "UserId", "ParticipantType", "ResourceType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageTranslations_InternalMessageId_TargetLanguage",
                table: "MessageTranslations",
                columns: new[] { "InternalMessageId", "TargetLanguage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlledResourceGrants");

            migrationBuilder.DropTable(
                name: "MessageTranslations");

            migrationBuilder.DropIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_ResourceType",
                table: "VerificationReviewRequests");

            migrationBuilder.DropIndex(
                name: "IX_VerificationReviewRequests_RequesterUserId_RequesterParticipantType_ResourceType_Status",
                table: "VerificationReviewRequests");

            migrationBuilder.DropColumn(
                name: "ResourceType",
                table: "VerificationReviewRequests");

            migrationBuilder.DropColumn(
                name: "PreferredCommunicationLanguage",
                table: "MobileProfileSettings");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "InternalMessages");

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
        }
    }
}
