using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFamilyScopedDependencyInventoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningRelationEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningPrimitiveEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningNodeEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageCompositionalAnchors_CurriculumFamilyId",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.CreateIndex(
                name: "IX_LegendMeaningRelationEvidence_FamilyActive",
                table: "LegendLanguageMeaningRelationEvidence",
                columns: new[] { "CurriculumFamilyId", "SupersededUtc" })
                .Annotation("SqlServer:Include", new[] { "MeaningRelationId", "EvidenceIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendMeaningPrimitiveEvidence_FamilyActive",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                columns: new[] { "CurriculumFamilyId", "SupersededUtc" })
                .Annotation("SqlServer:Include", new[] { "MeaningPrimitiveId", "MeaningNodeEvidenceId", "EvidenceIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendMeaningNodes_FamilyActive",
                table: "LegendLanguageMeaningNodeEvidence",
                columns: new[] { "CurriculumFamilyId", "SupersededUtc" })
                .Annotation("SqlServer:Include", new[] { "CurriculumExampleId", "NodeKey", "SemanticSignature" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCompositionalAnchors_FamilyActive",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "CurriculumFamilyId", "SupersededUtc" })
                .Annotation("SqlServer:Include", new[] { "CurriculumExampleId", "AnchorSignature", "SemanticSignature" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendMeaningRelationEvidence_FamilyActive",
                table: "LegendLanguageMeaningRelationEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendMeaningPrimitiveEvidence_FamilyActive",
                table: "LegendLanguageMeaningPrimitiveEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendMeaningNodes_FamilyActive",
                table: "LegendLanguageMeaningNodeEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendCompositionalAnchors_FamilyActive",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningRelationEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningNodeEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_CurriculumFamilyId",
                table: "LegendLanguageCompositionalAnchors",
                column: "CurriculumFamilyId");
        }
    }
}
