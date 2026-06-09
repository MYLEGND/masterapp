using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddAnalyticsEventTimeZoneLanguageColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 40,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "AnalyticsEvents");
        }
    }
}
