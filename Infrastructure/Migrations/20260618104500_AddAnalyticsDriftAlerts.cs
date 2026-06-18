using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260618104500_AddAnalyticsDriftAlerts")]
public partial class AddAnalyticsDriftAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnalyticsDriftAlerts",
            columns: table => new
            {
                Id = table.Column<long>(nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                IncidentKey = table.Column<string>(maxLength: 160, nullable: false),
                MetricKey = table.Column<string>(maxLength: 120, nullable: false),
                EventType = table.Column<string>(maxLength: 160, nullable: false),
                Category = table.Column<string>(maxLength: 80, nullable: false),
                Severity = table.Column<string>(maxLength: 32, nullable: false),
                MetricUnit = table.Column<string>(maxLength: 32, nullable: false),
                CurrentValue = table.Column<decimal>(precision: 18, scale: 4, nullable: false),
                BaselineValue = table.Column<decimal>(precision: 18, scale: 4, nullable: false),
                DeviationPercent = table.Column<decimal>(precision: 18, scale: 4, nullable: false),
                ScopeKey = table.Column<string>(maxLength: 120, nullable: false),
                IsActive = table.Column<bool>(nullable: false),
                WindowStartUtc = table.Column<DateTime>(nullable: false),
                WindowEndUtc = table.Column<DateTime>(nullable: false),
                FirstDetectedUtc = table.Column<DateTime>(nullable: false),
                LastDetectedUtc = table.Column<DateTime>(nullable: false),
                ObservedUtc = table.Column<DateTime>(nullable: false),
                ResolvedUtc = table.Column<DateTime>(nullable: true),
                LastNotifiedUtc = table.Column<DateTime>(nullable: true),
                Summary = table.Column<string>(maxLength: 500, nullable: true),
                DetailsJson = table.Column<string>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnalyticsDriftAlerts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_EventType",
            table: "AnalyticsDriftAlerts",
            column: "EventType");

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_IncidentKey",
            table: "AnalyticsDriftAlerts",
            column: "IncidentKey");

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_IsActive",
            table: "AnalyticsDriftAlerts",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_IsActive_Severity_ObservedUtc",
            table: "AnalyticsDriftAlerts",
            columns: new[] { "IsActive", "Severity", "ObservedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_ObservedUtc",
            table: "AnalyticsDriftAlerts",
            column: "ObservedUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_ScopeKey_ObservedUtc",
            table: "AnalyticsDriftAlerts",
            columns: new[] { "ScopeKey", "ObservedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsDriftAlerts_Severity",
            table: "AnalyticsDriftAlerts",
            column: "Severity");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AnalyticsDriftAlerts");
    }
}
