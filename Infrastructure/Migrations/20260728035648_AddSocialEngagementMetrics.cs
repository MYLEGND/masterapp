using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialEngagementMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RepostOfSocialPostId",
                table: "SocialPosts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentCommentId",
                table: "SocialPostComments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSocialPostId",
                table: "SocialFollows",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialPostMusicAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderTrackId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TrackTitle = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ArtistName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TrackDurationSeconds = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: false),
                    PreviewUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    TrimStartSeconds = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: false),
                    TrimEndSeconds = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: false),
                    MusicVolume = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    OriginalAudioVolume = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostMusicAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostMusicAttachments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostReposts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostReposts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostReposts_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostSaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostSaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostSaves_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostShares_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ViewerParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FirstViewedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastViewedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MaximumWatchDurationSeconds = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: true),
                    MaximumWatchCompletionPercentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    StoryExitCount = table.Column<int>(type: "int", nullable: false),
                    StoryTapForwardCount = table.Column<int>(type: "int", nullable: false),
                    StoryTapBackwardCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostViews_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialProfileVisits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TargetParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    VisitorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    VisitorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceSocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstVisitedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastVisitedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialProfileVisits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_RepostOfSocialPostId",
                table: "SocialPosts",
                column: "RepostOfSocialPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostComments_ParentCommentId",
                table: "SocialPostComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_SourceSocialPostId",
                table: "SocialFollows",
                column: "SourceSocialPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMusicAttachments_ProviderId_ProviderTrackId",
                table: "SocialPostMusicAttachments",
                columns: new[] { "ProviderId", "ProviderTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMusicAttachments_SocialPostId",
                table: "SocialPostMusicAttachments",
                column: "SocialPostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReposts_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostReposts",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReposts_SocialPostId_CreatedUtc",
                table: "SocialPostReposts",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostSaves_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostSaves",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostSaves_SocialPostId_CreatedUtc",
                table: "SocialPostSaves",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostShares_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostShares",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostShares_SocialPostId_CreatedUtc",
                table: "SocialPostShares",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostViews_SocialPostId_FirstViewedUtc",
                table: "SocialPostViews",
                columns: new[] { "SocialPostId", "FirstViewedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostViews_SocialPostId_ViewerUserId_ViewerParticipantType",
                table: "SocialPostViews",
                columns: new[] { "SocialPostId", "ViewerUserId", "ViewerParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialProfileVisits_TargetUserId_TargetParticipantType_FirstVisitedUtc",
                table: "SocialProfileVisits",
                columns: new[] { "TargetUserId", "TargetParticipantType", "FirstVisitedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialProfileVisits_TargetUserId_TargetParticipantType_VisitorUserId_VisitorParticipantType_SourceSocialPostId",
                table: "SocialProfileVisits",
                columns: new[] { "TargetUserId", "TargetParticipantType", "VisitorUserId", "VisitorParticipantType", "SourceSocialPostId" },
                unique: true);

            // SQLite cannot rebuild the pre-existing SocialPostComments table
            // because its historic Body column was created as nvarchar(max).
            // The relational model and application ownership validation remain
            // the authority in local SQLite; SQL Server receives the FK below.
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.AddForeignKey(
                    name: "FK_SocialPostComments_SocialPostComments_ParentCommentId",
                    table: "SocialPostComments",
                    column: "ParentCommentId",
                    principalTable: "SocialPostComments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.DropForeignKey(
                    name: "FK_SocialPostComments_SocialPostComments_ParentCommentId",
                    table: "SocialPostComments");
            }

            migrationBuilder.DropTable(
                name: "SocialPostMusicAttachments");

            migrationBuilder.DropTable(
                name: "SocialPostReposts");

            migrationBuilder.DropTable(
                name: "SocialPostSaves");

            migrationBuilder.DropTable(
                name: "SocialPostShares");

            migrationBuilder.DropTable(
                name: "SocialPostViews");

            migrationBuilder.DropTable(
                name: "SocialProfileVisits");

            migrationBuilder.DropIndex(
                name: "IX_SocialPosts_RepostOfSocialPostId",
                table: "SocialPosts");

            migrationBuilder.DropIndex(
                name: "IX_SocialPostComments_ParentCommentId",
                table: "SocialPostComments");

            migrationBuilder.DropIndex(
                name: "IX_SocialFollows_SourceSocialPostId",
                table: "SocialFollows");

            migrationBuilder.DropColumn(
                name: "RepostOfSocialPostId",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "ParentCommentId",
                table: "SocialPostComments");

            migrationBuilder.DropColumn(
                name: "SourceSocialPostId",
                table: "SocialFollows");
        }
    }
}
