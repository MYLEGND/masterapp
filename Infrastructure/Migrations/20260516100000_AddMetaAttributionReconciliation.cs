using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaAttributionReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UtmId",
                table: "AnalyticsEvents",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdId",
                table: "WebsiteLeads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdSetId",
                table: "WebsiteLeads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaCampaignId",
                table: "WebsiteLeads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmId",
                table: "WebsiteLeads",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmId",
                table: "AnalyticsEvents",
                column: "UtmId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_MetaCampaignId",
                table: "WebsiteLeads",
                column: "MetaCampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmId",
                table: "WebsiteLeads",
                column: "UtmId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_UtmId",
                table: "AnalyticsEvents");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_MetaCampaignId",
                table: "WebsiteLeads");

            migrationBuilder.DropIndex(
                name: "IX_WebsiteLeads_UtmId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "UtmId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "MetaAdSetId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "MetaCampaignId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "UtmId",
                table: "WebsiteLeads");
        }
    }
}
