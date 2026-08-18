using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendClosedLoopContinualLearningSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NeuralModelFailureCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "NeuralModelServeCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProviderObservationReuseCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeuralModelFailureCount",
                table: "LegendTranslationPairDemands");

            migrationBuilder.DropColumn(
                name: "NeuralModelServeCount",
                table: "LegendTranslationPairDemands");

            migrationBuilder.DropColumn(
                name: "ProviderObservationReuseCount",
                table: "LegendTranslationPairDemands");
        }
    }
}
