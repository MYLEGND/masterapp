using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260327121010_CreateAnalyticsEventsManual")]
    public partial class CreateAnalyticsEventsManual : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PageKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SectionKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ElementKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ButtonLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FormKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    QuoteType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Referrer = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    IsInternal = table.Column<bool>(type: "bit", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Host = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    EventUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmitOutcome = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ElementKey",
                table: "AnalyticsEvents",
                column: "ElementKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_EventType",
                table: "AnalyticsEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_FormKey",
                table: "AnalyticsEvents",
                column: "FormKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_PageKey",
                table: "AnalyticsEvents",
                column: "PageKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ReceivedUtc",
                table: "AnalyticsEvents",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_SessionId",
                table: "AnalyticsEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_VisitorId",
                table: "AnalyticsEvents",
                column: "VisitorId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsEvents");
        }
    }
}
