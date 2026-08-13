using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260526214309_AddAnalyticsEventSchemaTrackingVersion")]
    public partial class AddAnalyticsEventSchemaTrackingVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.AddColumn<int>(
                    name: "SchemaVersion",
                    table: "AnalyticsEvents",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: 1);

                migrationBuilder.AddColumn<string>(
                    name: "TrackingVersion",
                    table: "AnalyticsEvents",
                    type: "TEXT",
                    maxLength: 80,
                    nullable: true);

                return;
            }

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
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.DropColumn(name: "TrackingVersion", table: "AnalyticsEvents");
                migrationBuilder.DropColumn(name: "SchemaVersion", table: "AnalyticsEvents");
                return;
            }

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
