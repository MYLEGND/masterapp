using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class RepairAnalyticsEventsLanguageTimeZoneSqlServer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('AnalyticsEvents', 'TimeZone') IS NULL
                    ALTER TABLE [AnalyticsEvents] ADD [TimeZone] nvarchar(100) NULL;

                IF COL_LENGTH('AnalyticsEvents', 'Language') IS NULL
                    ALTER TABLE [AnalyticsEvents] ADD [Language] nvarchar(40) NULL;
            """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('AnalyticsEvents', 'Language') IS NOT NULL
                    ALTER TABLE [AnalyticsEvents] DROP COLUMN [Language];

                IF COL_LENGTH('AnalyticsEvents', 'TimeZone') IS NOT NULL
                    ALTER TABLE [AnalyticsEvents] DROP COLUMN [TimeZone];
            """);
        }
    }
}
