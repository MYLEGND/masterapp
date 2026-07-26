using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendSocialFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                CreateSqliteSchema(migrationBuilder);
                return;
            }

            CreateSqlServerSchema(migrationBuilder);
        }

        private static void CreateSqliteSchema(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialFollows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FollowerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FollowerParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FollowedUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FollowedParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialFollows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostComments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReactionType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostReactions_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            CreateIndexes(migrationBuilder);
        }

        private static void CreateSqlServerSchema(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialFollows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FollowerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FollowerParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FollowedUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FollowedParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialFollows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PostedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostComments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReactionType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostReactions_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            CreateIndexes(migrationBuilder);
        }

        private static void CreateIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType",
                table: "SocialFollows",
                columns: new[] { "FollowedUserId", "FollowedParticipantType" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowerUserId_FollowerParticipantType_FollowedUserId_FollowedParticipantType",
                table: "SocialFollows",
                columns: new[] { "FollowerUserId", "FollowerParticipantType", "FollowedUserId", "FollowedParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostComments_SocialPostId_DeletedUtc_CreatedUtc",
                table: "SocialPostComments",
                columns: new[] { "SocialPostId", "DeletedUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReactions_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostReactions",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_AuthorUserId_AuthorParticipantType_PostedUtc",
                table: "SocialPosts",
                columns: new[] { "AuthorUserId", "AuthorParticipantType", "PostedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_DeletedUtc_ExpiresUtc_PostedUtc",
                table: "SocialPosts",
                columns: new[] { "DeletedUtc", "ExpiresUtc", "PostedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialFollows");

            migrationBuilder.DropTable(
                name: "SocialPostComments");

            migrationBuilder.DropTable(
                name: "SocialPostReactions");

            migrationBuilder.DropTable(
                name: "SocialPosts");
        }
    }
}
