using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFounderPageScaleIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LegendLearningEvents_FounderSource",
                table: "LegendTranslationLearningEvents",
                columns: new[] { "SourceLanguageCode", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLearningEvents_FounderTarget",
                table: "LegendTranslationLearningEvents",
                columns: new[] { "TargetLanguageCode", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTextUnits_FounderLanguage",
                table: "LegendLanguageTextUnits",
                columns: new[] { "LanguageCode", "IsTrainingEligible", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTargetCandidates_FounderSource",
                table: "LegendLanguageTargetRealizationCandidates",
                columns: new[] { "SourceLanguageCode", "SupersededUtc", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTargetCandidates_FounderTarget",
                table: "LegendLanguageTargetRealizationCandidates",
                columns: new[] { "TargetLanguageCode", "SupersededUtc", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendStructuralRelationships_FounderLanguage",
                table: "LegendLanguageStructuralRelationships",
                columns: new[] { "LanguageCode", "SupersededUtc", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCompositionalAnchors_FounderLanguage",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "LanguageCode", "SupersededUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_FounderLanguage",
                table: "LegendCurriculumExamples",
                columns: new[] { "LanguageCode", "SupersededUtc", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLearningEvents_FounderSource",
                table: "LegendTranslationLearningEvents");

            migrationBuilder.DropIndex(
                name: "IX_LegendLearningEvents_FounderTarget",
                table: "LegendTranslationLearningEvents");

            migrationBuilder.DropIndex(
                name: "IX_LegendTextUnits_FounderLanguage",
                table: "LegendLanguageTextUnits");

            migrationBuilder.DropIndex(
                name: "IX_LegendTargetCandidates_FounderSource",
                table: "LegendLanguageTargetRealizationCandidates");

            migrationBuilder.DropIndex(
                name: "IX_LegendTargetCandidates_FounderTarget",
                table: "LegendLanguageTargetRealizationCandidates");

            migrationBuilder.DropIndex(
                name: "IX_LegendStructuralRelationships_FounderLanguage",
                table: "LegendLanguageStructuralRelationships");

            migrationBuilder.DropIndex(
                name: "IX_LegendCompositionalAnchors_FounderLanguage",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.DropIndex(
                name: "IX_LegendCurriculumExamples_FounderLanguage",
                table: "LegendCurriculumExamples");
        }
    }
}
