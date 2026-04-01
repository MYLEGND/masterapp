using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations
{
    public partial class AddAnalyticsScaleIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlite = migrationBuilder.ActiveProvider?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            if (isSqlite)
            {
                // Dev/local SQLite already has simple indexes; skip provider-specific additions.
                return;
            }

            // AnalyticsEvents
            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmSource",
                table: "AnalyticsEvents",
                column: "UtmSource");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmCampaign",
                table: "AnalyticsEvents",
                column: "UtmCampaign");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_AgentTrackingProfileId_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "AgentTrackingProfileId", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_Environment_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "Environment", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_EventType_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "EventType", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_PageKey_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "PageKey", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ElementKey_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "ElementKey", "EventUtc" });

            // WebsiteLeads
            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_CreatedUtc",
                table: "WebsiteLeads",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_AgentTrackingProfileId_CreatedUtc",
                table: "WebsiteLeads",
                columns: new[] { "AgentTrackingProfileId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_Environment_CreatedUtc",
                table: "WebsiteLeads",
                columns: new[] { "Environment", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourcePageKey",
                table: "WebsiteLeads",
                column: "SourcePageKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourceCtaKey",
                table: "WebsiteLeads",
                column: "SourceCtaKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmSource",
                table: "WebsiteLeads",
                column: "UtmSource");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmCampaign",
                table: "WebsiteLeads",
                column: "UtmCampaign");
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
