using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260327121000_CreateWebsiteLeadManual")]
    public partial class CreateWebsiteLeadManual : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebsiteLeads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PreferredContactMethod = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    InterestType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    SourcePageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SourceCtaKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmSource = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    MarketingEmailConsent = table.Column<bool>(type: "bit", nullable: false),
                    CallTextConsent = table.Column<bool>(type: "bit", nullable: false),
                    TermsAccepted = table.Column<bool>(type: "bit", nullable: false),
                    IsInternal = table.Column<bool>(type: "bit", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebsiteLeads", x => x.Id);
            });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_CreatedUtc",
                table: "WebsiteLeads",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_Email",
                table: "WebsiteLeads",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_InterestType",
                table: "WebsiteLeads",
                column: "InterestType");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourceCtaKey",
                table: "WebsiteLeads",
                column: "SourceCtaKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourcePageKey",
                table: "WebsiteLeads",
                column: "SourcePageKey");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebsiteLeads");
        }
    }
}
