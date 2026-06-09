using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260413000000_AddFbclidColumns")]
    public partial class AddFbclidColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fbclid",
                table: "AnalyticsEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbclid",
                table: "WebsiteLeads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fbclid",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Fbclid",
                table: "WebsiteLeads");
        }
    }
}
