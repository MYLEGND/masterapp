using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260517120000_AddMetaSignalIntelligence")]
public partial class AddMetaSignalIntelligence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MetaSignalEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                EventId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                EventName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                EventCategory = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                SessionId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                VisitorId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                QuoteType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                PageKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                EffectivePageKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                PageVariant = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                PageMode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                TrafficType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                FunnelStep = table.Column<int>(type: "int", nullable: true),
                StepName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                IntentScore = table.Column<int>(type: "int", nullable: false),
                EngagementScore = table.Column<int>(type: "int", nullable: false),
                QualificationScore = table.Column<int>(type: "int", nullable: false),
                FrictionScore = table.Column<int>(type: "int", nullable: false),
                TotalSignalScore = table.Column<int>(type: "int", nullable: false),
                ScoreTier = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                MetaBrowserSent = table.Column<bool>(type: "bit", nullable: false),
                MetaServerSent = table.Column<bool>(type: "bit", nullable: false),
                MetaDeduplicationKey = table.Column<string>(type: "nvarchar(220)", maxLength: 220, nullable: true),
                UtmSource = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                UtmMedium = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                UtmCampaign = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                UtmId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                UtmContent = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                FbclidPresent = table.Column<bool>(type: "bit", nullable: false),
                FbcPresent = table.Column<bool>(type: "bit", nullable: false),
                FbpPresent = table.Column<bool>(type: "bit", nullable: false),
                Referrer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                UserAgentHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                IpHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                AgentTrackingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AgentSlug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Environment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                Host = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MetaSignalEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_AgentSlug",
            table: "MetaSignalEvents",
            column: "AgentSlug");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_AgentTrackingProfileId",
            table: "MetaSignalEvents",
            column: "AgentTrackingProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_CreatedUtc",
            table: "MetaSignalEvents",
            column: "CreatedUtc");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_EventId",
            table: "MetaSignalEvents",
            column: "EventId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_EventName",
            table: "MetaSignalEvents",
            column: "EventName");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_EventName_CreatedUtc",
            table: "MetaSignalEvents",
            columns: new[] { "EventName", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_LeadId",
            table: "MetaSignalEvents",
            column: "LeadId");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_QuoteType",
            table: "MetaSignalEvents",
            column: "QuoteType");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_ScoreTier",
            table: "MetaSignalEvents",
            column: "ScoreTier");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_SessionId",
            table: "MetaSignalEvents",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_SessionId_QuoteType_CreatedUtc",
            table: "MetaSignalEvents",
            columns: new[] { "SessionId", "QuoteType", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_TrafficType",
            table: "MetaSignalEvents",
            column: "TrafficType");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_UtmCampaign",
            table: "MetaSignalEvents",
            column: "UtmCampaign");

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_VisitorId",
            table: "MetaSignalEvents",
            column: "VisitorId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MetaSignalEvents");
    }
}
