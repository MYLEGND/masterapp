using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations
{
    public partial class AddAnalyticsScaleIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlite = migrationBuilder.ActiveProvider?.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase) ?? false;

            if (isSqlite)
            {
                // Dev/local SQLite already has simple indexes; skip provider-specific additions.
                return;
            }

            var isSqlServer = migrationBuilder.ActiveProvider?.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase) ?? false;

            if (!isSqlServer)
            {
                throw new InvalidOperationException(
                    $"Unsupported provider for analytics scale index migration: {migrationBuilder.ActiveProvider}");
            }

            void CreateIndexIfMissing(
                string name,
                string table,
                params string[] columns)
            {
                var columnSql = string.Join(
                    ", ",
                    columns.Select(column => $"[{column.Replace("]", "]]")}]"));

                var escapedName = name.Replace("'", "''");
                var escapedTable = table.Replace("'", "''");
                var quotedName = name.Replace("]", "]]");
                var quotedTable = table.Replace("]", "]]");

                migrationBuilder.Sql($"""
IF OBJECT_ID(N'[dbo].[{quotedTable}]', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes
       WHERE name = N'{escapedName}'
         AND object_id = OBJECT_ID(N'[dbo].[{quotedTable}]')
   )
BEGIN
    CREATE INDEX [{quotedName}]
        ON [dbo].[{quotedTable}] ({columnSql});
END
""");
            }

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_UtmSource",
                "AnalyticsEvents",
                "UtmSource");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_UtmCampaign",
                "AnalyticsEvents",
                "UtmCampaign");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_AgentTrackingProfileId_EventUtc",
                "AnalyticsEvents",
                "AgentTrackingProfileId",
                "EventUtc");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_Environment_EventUtc",
                "AnalyticsEvents",
                "Environment",
                "EventUtc");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_EventType_EventUtc",
                "AnalyticsEvents",
                "EventType",
                "EventUtc");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_PageKey_EventUtc",
                "AnalyticsEvents",
                "PageKey",
                "EventUtc");

            CreateIndexIfMissing(
                "IX_AnalyticsEvents_ElementKey_EventUtc",
                "AnalyticsEvents",
                "ElementKey",
                "EventUtc");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_CreatedUtc",
                "WebsiteLeads",
                "CreatedUtc");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_AgentTrackingProfileId_CreatedUtc",
                "WebsiteLeads",
                "AgentTrackingProfileId",
                "CreatedUtc");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_Environment_CreatedUtc",
                "WebsiteLeads",
                "Environment",
                "CreatedUtc");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_SourcePageKey",
                "WebsiteLeads",
                "SourcePageKey");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_SourceCtaKey",
                "WebsiteLeads",
                "SourceCtaKey");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_UtmSource",
                "WebsiteLeads",
                "UtmSource");

            CreateIndexIfMissing(
                "IX_WebsiteLeads_UtmCampaign",
                "WebsiteLeads",
                "UtmCampaign");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var isSqlite = migrationBuilder.ActiveProvider?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            if (isSqlite)
            {
                return;
            }

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_UtmSource",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_UtmCampaign",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_AgentTrackingProfileId_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_Environment_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_EventType_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_PageKey_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_ElementKey_EventUtc",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_CreatedUtc",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_AgentTrackingProfileId_CreatedUtc",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_Environment_CreatedUtc",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_SourcePageKey",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_SourceCtaKey",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_UtmSource",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_UtmCampaign",
                table: "WebsiteLeads");
        }
    }
}
