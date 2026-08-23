using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendCanonicalMutationLaneFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalMutationLane",
                table: "LegendHistoricalReevaluationWorkItems",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendHistoricalReevaluationWorkItems_ActiveCanonicalMutationLane",
                table: "LegendHistoricalReevaluationWorkItems",
                column: "CanonicalMutationLane",
                unique: true,
                filter: "[ProcessingState] = 'Processing' AND [CanonicalMutationLane] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendHistoricalReevaluationWorkItems_ActiveCanonicalMutationLane",
                table: "LegendHistoricalReevaluationWorkItems");

            migrationBuilder.DropColumn(
                name: "CanonicalMutationLane",
                table: "LegendHistoricalReevaluationWorkItems");
        }
    }
}
