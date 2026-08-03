using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialMediaPublicationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicationState",
                table: "SocialPosts",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Published");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_PublicationState_DeletedUtc_ExpiresUtc_PostedUtc",
                table: "SocialPosts",
                columns: new[] { "PublicationState", "DeletedUtc", "ExpiresUtc", "PostedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SocialPosts_PublicationState_DeletedUtc_ExpiresUtc_PostedUtc",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "PublicationState",
                table: "SocialPosts");
        }
    }
}
