using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260524025508_AddAnalyticsEventTimeZoneLanguageColumns")]
    public partial class AddAnalyticsEventTimeZoneLanguageColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "AnalyticsEvents",
                type: isSqlServer ? "nvarchar(100)" : "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AnalyticsEvents",
                type: isSqlServer ? "nvarchar(40)" : "TEXT",
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
