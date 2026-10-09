using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260927013000_AddOpenAiClickReferenceLineage")]
public partial class AddOpenAiClickReferenceLineage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Oppref", "AnalyticsEvents", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>("Oppref", "WebsiteLeads", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>("Oppref", "WebsiteLeadIntakeLinks", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>("Oppref", "LeadAppointments", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>("Oppref", "CommerceOrders", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>("Oppref", "ProductionRecords", maxLength: 1024, nullable: true);

        migrationBuilder.CreateIndex("IX_AnalyticsEvents_Oppref", "AnalyticsEvents", "Oppref");
        migrationBuilder.CreateIndex("IX_WebsiteLeads_Oppref", "WebsiteLeads", "Oppref");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AnalyticsEvents_Oppref", "AnalyticsEvents");
        migrationBuilder.DropIndex("IX_WebsiteLeads_Oppref", "WebsiteLeads");
        migrationBuilder.DropColumn("Oppref", "AnalyticsEvents");
        migrationBuilder.DropColumn("Oppref", "WebsiteLeads");
        migrationBuilder.DropColumn("Oppref", "WebsiteLeadIntakeLinks");
        migrationBuilder.DropColumn("Oppref", "LeadAppointments");
        migrationBuilder.DropColumn("Oppref", "CommerceOrders");
        migrationBuilder.DropColumn("Oppref", "ProductionRecords");
    }
}
