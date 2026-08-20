using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendHistoricalCapabilityReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendFounderTrainingSubmissions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LanguageIntelligenceReevaluationLeaseExpiresUtc",
                table: "LegendFounderTrainingSubmissions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendCurriculumManifestWorkItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TargetLanguageIntelligenceEvaluatorVersion",
                table: "LegendCurriculumManifestWorkItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissions_CapabilityReplay",
                table: "LegendFounderTrainingSubmissions",
                columns: new[] { "SourceLanguageCode", "CompletedLanguageIntelligenceEvaluatorVersion", "LanguageIntelligenceReevaluationLeaseExpiresUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumManifestWorkItems_CapabilityReplay",
                table: "LegendCurriculumManifestWorkItems",
                columns: new[] { "ProcessingState", "CompletedLanguageIntelligenceEvaluatorVersion", "LeaseExpiresUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendFounderTrainingSubmissions_CapabilityReplay",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_LegendCurriculumManifestWorkItems_CapabilityReplay",
                table: "LegendCurriculumManifestWorkItems");

            migrationBuilder.DropColumn(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropColumn(
                name: "LanguageIntelligenceReevaluationLeaseExpiresUtc",
                table: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropColumn(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendCurriculumManifestWorkItems");

            migrationBuilder.DropColumn(
                name: "TargetLanguageIntelligenceEvaluatorVersion",
                table: "LegendCurriculumManifestWorkItems");
        }
    }
}
