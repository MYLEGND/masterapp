using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    public partial class AddAnalyticsEventSchemaTrackingVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF COL_LENGTH('AnalyticsEvents', 'SchemaVersion') IS NULL
                    ALTER TABLE [AnalyticsEvents]
                    ADD [SchemaVersion] int NOT NULL
                    CONSTRAINT [DF_AnalyticsEvents_SchemaVersion] DEFAULT 1;

                IF COL_LENGTH('AnalyticsEvents', 'TrackingVersion') IS NULL
                    ALTER TABLE [AnalyticsEvents]
                    ADD [TrackingVersion] nvarchar(80) NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF COL_LENGTH('AnalyticsEvents', 'TrackingVersion') IS NOT NULL
                    ALTER TABLE [AnalyticsEvents]
                    DROP COLUMN [TrackingVersion];

                IF COL_LENGTH('AnalyticsEvents', 'SchemaVersion') IS NOT NULL
                    ALTER TABLE [AnalyticsEvents]
                    DROP CONSTRAINT [DF_AnalyticsEvents_SchemaVersion];

                IF COL_LENGTH('AnalyticsEvents', 'SchemaVersion') IS NOT NULL
                    ALTER TABLE [AnalyticsEvents]
                    DROP COLUMN [SchemaVersion];
            ");
        }
    }
}
