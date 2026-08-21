using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFounderSubmissionProcessingVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewCanonicalUnitCount",
                table: "LegendFounderTrainingSubmissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueuedCoverageCount",
                table: "LegendFounderTrainingSubmissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReusedCanonicalUnitCount",
                table: "LegendFounderTrainingSubmissions",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissions_FounderStatus",
                table: "LegendFounderTrainingSubmissions",
                columns: new[] { "SourceLanguageCode", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
                table: "LegendCurriculumManifestWorkItems",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendFounderTrainingSubmissions_FounderStatus",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
                table: "LegendCurriculumManifestWorkItems");

            migrationBuilder.DropColumn(
                name: "NewCanonicalUnitCount",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropColumn(
                name: "QueuedCoverageCount",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropColumn(
                name: "ReusedCanonicalUnitCount",
                table: "LegendFounderTrainingSubmissions");
        }
    }
}
