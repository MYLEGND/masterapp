using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectCorpusIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_SourceTextUnitId",
                table: "LegendTranslationAlignments",
                column: "SourceTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_TargetTextUnitId",
                table: "LegendTranslationAlignments",
                column: "TargetTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTextUnits_GlobalConceptId",
                table: "LegendLanguageTextUnits",
                column: "GlobalConceptId");

            migrationBuilder.AddForeignKey(
                name: "FK_LegendLanguageTextUnits_LegendGlobalConcepts_GlobalConceptId",
                table: "LegendLanguageTextUnits",
                column: "GlobalConceptId",
                principalTable: "LegendGlobalConcepts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_LegendTranslationAlignments_LegendLanguageTextUnits_SourceTextUnitId",
                table: "LegendTranslationAlignments",
                column: "SourceTextUnitId",
                principalTable: "LegendLanguageTextUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LegendTranslationAlignments_LegendLanguageTextUnits_TargetTextUnitId",
                table: "LegendTranslationAlignments",
                column: "TargetTextUnitId",
                principalTable: "LegendLanguageTextUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegendLanguageTextUnits_LegendGlobalConcepts_GlobalConceptId",
                table: "LegendLanguageTextUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_LegendTranslationAlignments_LegendLanguageTextUnits_SourceTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropForeignKey(
                name: "FK_LegendTranslationAlignments_LegendLanguageTextUnits_TargetTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_SourceTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_TargetTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageTextUnits_GlobalConceptId",
                table: "LegendLanguageTextUnits");
        }
    }
}
