using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddMetaIdentityToWebsiteLeadIntakeLinks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientIpAddress",
                table: "WebsiteLeadIntakeLinks",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "WebsiteLeadIntakeLinks",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbc",
                table: "WebsiteLeadIntakeLinks",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbp",
                table: "WebsiteLeadIntakeLinks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientIpAddress",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "ClientUserAgent",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "Fbc",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "Fbp",
                table: "WebsiteLeadIntakeLinks");
        }
    }
}
