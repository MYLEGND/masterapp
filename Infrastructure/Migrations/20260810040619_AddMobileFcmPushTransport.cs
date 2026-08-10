using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileFcmPushTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MobilePushDevices_TokenHash",
                table: "MobilePushDevices");

            migrationBuilder.AlterColumn<string>(
                name: "DeviceToken",
                table: "MobilePushDevices",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "MobilePushDevices",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "apns");

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDevices_Provider_TokenHash",
                table: "MobilePushDevices",
                columns: new[] { "Provider", "TokenHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MobilePushDevices_Provider_TokenHash",
                table: "MobilePushDevices");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "MobilePushDevices");

            migrationBuilder.AlterColumn<string>(
                name: "DeviceToken",
                table: "MobilePushDevices",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldMaxLength: 4096);

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDevices_TokenHash",
                table: "MobilePushDevices",
                column: "TokenHash",
                unique: true);
        }
    }
}
