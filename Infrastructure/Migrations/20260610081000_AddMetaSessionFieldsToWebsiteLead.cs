using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddMetaSessionFieldsToWebsiteLead : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientIpAddress",
                table: "WebsiteLeads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "WebsiteLeads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbp",
                table: "WebsiteLeads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbc",
                table: "WebsiteLeads",
                type: "nvarchar(max)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientIpAddress",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "ClientUserAgent",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "Fbp",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "Fbc",
                table: "WebsiteLeads");
        }
    }
}
