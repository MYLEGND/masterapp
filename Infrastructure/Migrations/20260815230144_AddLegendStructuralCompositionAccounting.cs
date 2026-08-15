using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendStructuralCompositionAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StructuralCompositionCharactersAvoided",
                table: "LegendTranslationUsagePeriods",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "StructuralCompositionCharactersAvoided",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "StructuralInternalServeCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StructuralCompositionCharactersAvoided",
                table: "LegendTranslationUsagePeriods");

            migrationBuilder.DropColumn(
                name: "StructuralCompositionCharactersAvoided",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "StructuralInternalServeCount",
                table: "LegendTranslationPairDemands");
        }
    }
}
