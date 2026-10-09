using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260927190000_CanonicalOpenAiAnalyticsDelivery")]
public sealed partial class CanonicalOpenAiAnalyticsDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>("AnalyticsEventId", "MarketingDestinationDeliveries", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>("MetaSignalEventId", "MarketingDestinationDeliveries", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<string>("AdvertiserAccountId", "MarketingDestinationDeliveries", type: "nvarchar(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("ConversionDataSourceId", "MarketingDestinationDeliveries", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.CreateIndex("IX_MarketingDestinationDeliveries_AnalyticsEventId", "MarketingDestinationDeliveries", "AnalyticsEventId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_MarketingDestinationDeliveries_AnalyticsEventId", "MarketingDestinationDeliveries");
        foreach (var column in new[] { "AnalyticsEventId", "MetaSignalEventId", "AdvertiserAccountId", "ConversionDataSourceId" })
            migrationBuilder.DropColumn(column, "MarketingDestinationDeliveries");
    }
}
