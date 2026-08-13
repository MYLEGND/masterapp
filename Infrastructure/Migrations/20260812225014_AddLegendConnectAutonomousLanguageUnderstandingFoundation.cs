using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectAutonomousLanguageUnderstandingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_CurriculumFamilyId_LanguageCode_VariationDimension_BaselineCurriculumExampleId_ComparedCurr~",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralPatternId_CreatedUtc",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.AddColumn<string>(
                name: "SemanticSignature",
                table: "LegendTranslationQualityEvidence",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Confidence",
                table: "LegendLanguageStructuralPatterns",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "HumanVerifiedSupportCount",
                table: "LegendLanguageStructuralPatterns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndependentSourceCount",
                table: "LegendLanguageStructuralPatterns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PairKey",
                table: "LegendLanguageStructuralPatterns",
                type: "nvarchar(72)",
                maxLength: 72,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ProviderOnlySupportCount",
                table: "LegendLanguageStructuralPatterns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContributionState",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IndependentSourceIdentity",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(96)",
                maxLength: 96,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsHumanVerifiedSupport",
                table: "LegendLanguageStructuralEvidence",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PairKey",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(72)",
                maxLength: 72,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SemanticSignature",
                table: "LegendLanguageCompositionalAnchors",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Existing structural rows predate pair-scoped independent
            // evidence. Preserve them historically, but remove their active
            // maturity until the canonical reevaluator reconstructs support
            // from lineage, provenance, and distinct source identities. This
            // prevents prior ProviderDerived promotion from becoming an
            // implicit production rule during the transition.
            if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
UPDATE [LegendLanguageStructuralPatterns]
SET [MaturityState] = N'Observation',
    [SupportCount] = 0,
    [ContradictionCount] = 0,
    [IndependentSourceCount] = 0,
    [HumanVerifiedSupportCount] = 0,
    [ProviderOnlySupportCount] = 0,
    [Confidence] = CAST(0 AS decimal(5,4)),
    [IsProductionEligible] = CAST(0 AS bit);

UPDATE [LegendLanguageStructuralEvidence]
SET [ContributionState] = N'Insufficient',
    [IsHumanVerifiedSupport] = CAST(0 AS bit); ");
            }
            else
            {
                migrationBuilder.Sql(@"
UPDATE ""LegendLanguageStructuralPatterns""
SET ""MaturityState"" = 'Observation',
    ""SupportCount"" = 0,
    ""ContradictionCount"" = 0,
    ""IndependentSourceCount"" = 0,
    ""HumanVerifiedSupportCount"" = 0,
    ""ProviderOnlySupportCount"" = 0,
    ""Confidence"" = 0,
    ""IsProductionEligible"" = 0;

UPDATE ""LegendLanguageStructuralEvidence""
SET ""ContributionState"" = 'Insufficient',
    ""IsHumanVerifiedSupport"" = 0; ");
            }

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_PairKey_SemanticSignature_Signal_SupersededUtc",
                table: "LegendTranslationQualityEvidence",
                columns: new[] { "PairKey", "SemanticSignature", "Signal", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "CurriculumFamilyId", "PairKey", "LanguageCode", "VariationDimension", "RealizationSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_PairKey_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "PairKey", "LanguageCode", "MaturityState", "IsProductionEligible" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_BaselineCurriculumExampleId_Comp~",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "CurriculumFamilyId", "PairKey", "LanguageCode", "VariationDimension", "BaselineCurriculumExampleId", "ComparedCurriculumExampleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralPatternId_ContributionState_SupersededUtc",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "StructuralPatternId", "ContributionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_SemanticSignature_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "SemanticSignature", "SupersededUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationQualityEvidence_PairKey_SemanticSignature_Signal_SupersededUtc",
                table: "LegendTranslationQualityEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_PairKey_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_BaselineCurriculumExampleId_Comp~",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralPatternId_ContributionState_SupersededUtc",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageCompositionalAnchors_SemanticSignature_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.DropColumn(
                name: "SemanticSignature",
                table: "LegendTranslationQualityEvidence");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "HumanVerifiedSupportCount",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "IndependentSourceCount",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "PairKey",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "ProviderOnlySupportCount",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "ContributionState",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "IndependentSourceIdentity",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "IsHumanVerifiedSupport",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "PairKey",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "SemanticSignature",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "CurriculumFamilyId", "LanguageCode", "VariationDimension", "RealizationSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "LanguageCode", "MaturityState", "IsProductionEligible" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_CurriculumFamilyId_LanguageCode_VariationDimension_BaselineCurriculumExampleId_ComparedCurr~",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "CurriculumFamilyId", "LanguageCode", "VariationDimension", "BaselineCurriculumExampleId", "ComparedCurriculumExampleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralPatternId_CreatedUtc",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "StructuralPatternId", "CreatedUtc" });
        }
    }
}
