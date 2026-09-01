using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Separates promoted translation-model avoidance from exact provider-output
/// reuse in both privacy-safe usage authorities. Neither column represents
/// Founder-chat reasoning or native coverage inferred from provider work.
/// </summary>
[DbContext(typeof(MasterAppDbContext))]
[Migration("20260831090000_ReconcileLegendTranslationServingStages")]
public sealed partial class ReconcileLegendTranslationServingStages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The former provider-observation branch incremented both counters.
        // ProviderObservationReuseCount is the exact durable amount that must
        // be removed from the mislabeled memory total; clamp defensively for
        // any partially repaired aggregate.
        migrationBuilder.Sql("""
            UPDATE [LegendTranslationPairDemands]
            SET [TranslationMemoryHitCount] =
                CASE
                    WHEN [TranslationMemoryHitCount] >= [ProviderObservationReuseCount]
                        THEN [TranslationMemoryHitCount] - [ProviderObservationReuseCount]
                    ELSE 0
                END
            WHERE [ProviderObservationReuseCount] > 0;
            """);

        migrationBuilder.AddColumn<long>(
            name: "PromotedTranslationModelCharactersAvoided",
            table: "LegendTranslationUsagePeriods",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "ProviderObservationCharactersAvoided",
            table: "LegendTranslationUsagePeriods",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "PromotedTranslationModelCharactersAvoided",
            table: "LegendTranslationSystemUsages",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "ProviderObservationCharactersAvoided",
            table: "LegendTranslationSystemUsages",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE [LegendTranslationPairDemands]
            SET [TranslationMemoryHitCount] =
                [TranslationMemoryHitCount] + [ProviderObservationReuseCount]
            WHERE [ProviderObservationReuseCount] > 0;
            """);

        migrationBuilder.DropColumn(
            name: "PromotedTranslationModelCharactersAvoided",
            table: "LegendTranslationUsagePeriods");

        migrationBuilder.DropColumn(
            name: "ProviderObservationCharactersAvoided",
            table: "LegendTranslationUsagePeriods");

        migrationBuilder.DropColumn(
            name: "PromotedTranslationModelCharactersAvoided",
            table: "LegendTranslationSystemUsages");

        migrationBuilder.DropColumn(
            name: "ProviderObservationCharactersAvoided",
            table: "LegendTranslationSystemUsages");
    }
}
