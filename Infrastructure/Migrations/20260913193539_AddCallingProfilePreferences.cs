using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCallingProfilePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallRingtoneId",
                table: "MobileProfileSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "signature");

            migrationBuilder.AddColumn<string>(
                name: "CallWallpaperMode",
                table: "MobileProfileSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "legend");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallRingtoneId",
                table: "MobileProfileSettings");

            migrationBuilder.DropColumn(
                name: "CallWallpaperMode",
                table: "MobileProfileSettings");
        }
    }
}
