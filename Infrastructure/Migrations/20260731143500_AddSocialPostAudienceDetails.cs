using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Adds the post-detail columns the Legend composer already collects: an author
    /// supplied place label and an author comment switch. Audience already exists as a
    /// column; this migration only widens the values it is allowed to hold.
    ///
    /// Both columns are additive. Existing rows keep comments enabled and no location,
    /// which matches the behaviour before this migration.
    /// </summary>
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260731143500_AddSocialPostAudienceDetails")]
    public partial class AddSocialPostAudienceDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlite = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "SocialPosts",
                type: isSqlite ? "TEXT" : "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CommentsEnabled",
                table: "SocialPosts",
                type: isSqlite ? "INTEGER" : "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommentsEnabled",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "SocialPosts");
        }
    }
}
