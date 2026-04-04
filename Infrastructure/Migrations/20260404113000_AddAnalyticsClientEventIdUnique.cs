using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260404113000_AddAnalyticsClientEventIdUnique")]
    public partial class AddAnalyticsClientEventIdUnique : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
WITH Deduped AS (
    SELECT
        Id,
        ROW_NUMBER() OVER (
            PARTITION BY ClientEventId
            ORDER BY ReceivedUtc ASC, Id ASC
        ) AS rn
    FROM AnalyticsEvents
    WHERE ClientEventId IS NOT NULL
)
DELETE FROM Deduped WHERE rn > 1;
""");
            }
            else
            {
                migrationBuilder.Sql("""
DELETE FROM AnalyticsEvents
WHERE Id IN (
    SELECT Id
    FROM (
        SELECT
            Id,
            ROW_NUMBER() OVER (
                PARTITION BY ClientEventId
                ORDER BY ReceivedUtc ASC, Id ASC
            ) AS rn
        FROM AnalyticsEvents
        WHERE ClientEventId IS NOT NULL
    ) AS dedupe
    WHERE dedupe.rn > 1
);
""");
            }

            migrationBuilder.CreateIndex(
                name: "UX_AnalyticsEvents_ClientEventId",
                table: "AnalyticsEvents",
                column: "ClientEventId",
                unique: true,
                filter: "[ClientEventId] IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AnalyticsEvents_ClientEventId",
                table: "AnalyticsEvents");
        }
    }
}
