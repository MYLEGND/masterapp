using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddAnalyticsForensicTrafficSignals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IpAddress') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [IpAddress] nvarchar(100) NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IsHeadless') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [IsHeadless] bit NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'MouseMoveCount') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [MouseMoveCount] int NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UserAgent') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [UserAgent] nvarchar(2048) NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'VisibilityChangeCount') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [VisibilityChangeCount] int NULL;

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'WebDriver') IS NULL
    ALTER TABLE [dbo].[AnalyticsEvents] ADD [WebDriver] bit NULL;
""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.AnalyticsEvents', N'WebDriver') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [WebDriver];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'VisibilityChangeCount') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [VisibilityChangeCount];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'UserAgent') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [UserAgent];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'MouseMoveCount') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [MouseMoveCount];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IsHeadless') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [IsHeadless];

IF COL_LENGTH(N'dbo.AnalyticsEvents', N'IpAddress') IS NOT NULL
    ALTER TABLE [dbo].[AnalyticsEvents] DROP COLUMN [IpAddress];
""");
        }
    }
}
