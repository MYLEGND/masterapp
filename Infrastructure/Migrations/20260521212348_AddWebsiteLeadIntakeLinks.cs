using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteLeadIntakeLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebsiteLeadIntakeLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebsiteLeadRowId = table.Column<long>(type: "bigint", nullable: false),
                    WebsiteLeadPublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkstationLeadId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AgentUserId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Bucket = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourcePageKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SourceCtaKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    PageVariant = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    PageMode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    PagePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LandingPageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReferrerUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InterestType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OfferKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ProductType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmTerm = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmContent = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    Fbclid = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    MetaCampaignId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    MetaAdSetId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    MetaAdId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    DiscoverySummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EstimateSummary = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    RecommendationPrimaryKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    RecommendationPrimaryTitle = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    RecommendationSecondaryKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    RecommendationSecondaryTitle = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteLeadIntakeLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebsiteLeadIntakeLinks_WebsiteLeads_WebsiteLeadRowId",
                        column: x => x.WebsiteLeadRowId,
                        principalTable: "WebsiteLeads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebsiteLeadIntakeLinks_WorkstationLeadProfiles_WorkstationLeadId",
                        column: x => x.WorkstationLeadId,
                        principalTable: "WorkstationLeadProfiles",
                        principalColumn: "LeadId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_AgentUserId_SubmittedUtc",
                table: "WebsiteLeadIntakeLinks",
                columns: new[] { "AgentUserId", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_WebsiteLeadRowId",
                table: "WebsiteLeadIntakeLinks",
                column: "WebsiteLeadRowId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_WorkstationLeadId_SubmittedUtc",
                table: "WebsiteLeadIntakeLinks",
                columns: new[] { "WorkstationLeadId", "SubmittedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebsiteLeadIntakeLinks");
        }
    }
}
