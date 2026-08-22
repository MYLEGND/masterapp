using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendGovernedDiscourseReferenceBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedBindingsJson",
                table: "LegendFounderAiDiscourseTurns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "LegendLanguageDiscourseReferenceRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RuleSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SelectorSemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntitySemanticDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ResolutionMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SelectionRank = table.Column<int>(type: "int", nullable: true),
                    AllowedSourceRoles = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ReplacesActiveBinding = table.Column<bool>(type: "bit", nullable: false),
                    MaturityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportCount = table.Column<int>(type: "int", nullable: false),
                    ContradictionCount = table.Column<int>(type: "int", nullable: false),
                    IndependentSourceCount = table.Column<int>(type: "int", nullable: false),
                    HumanVerifiedSupportCount = table.Column<int>(type: "int", nullable: false),
                    IsProductionEligible = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDiscourseReferenceRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageDiscourseReferenceRuleEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiscourseReferenceRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectorMeaningNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IndependentSourceIdentity = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    ContributionState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsHumanVerifiedSupport = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDiscourseReferenceRuleEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageDiscourseReferenceRuleEvidence_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageDiscourseReferenceRuleEvidence_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageDiscourseReferenceRuleEvidence_LegendLanguageDiscourseReferenceRules_DiscourseReferenceRuleId",
                        column: x => x.DiscourseReferenceRuleId,
                        principalTable: "LegendLanguageDiscourseReferenceRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageDiscourseReferenceRuleEvidence_LegendLanguageMeaningNodeEvidence_SelectorMeaningNodeId",
                        column: x => x.SelectorMeaningNodeId,
                        principalTable: "LegendLanguageMeaningNodeEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRuleEvidence_CurriculumExampleId_SupersededUtc",
                table: "LegendLanguageDiscourseReferenceRuleEvidence",
                columns: new[] { "CurriculumExampleId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRuleEvidence_CurriculumFamilyId",
                table: "LegendLanguageDiscourseReferenceRuleEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRuleEvidence_DiscourseReferenceRuleId_ContributionState_SupersededUtc",
                table: "LegendLanguageDiscourseReferenceRuleEvidence",
                columns: new[] { "DiscourseReferenceRuleId", "ContributionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRuleEvidence_EvidenceIdentity",
                table: "LegendLanguageDiscourseReferenceRuleEvidence",
                column: "EvidenceIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRuleEvidence_SelectorMeaningNodeId",
                table: "LegendLanguageDiscourseReferenceRuleEvidence",
                column: "SelectorMeaningNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRules_LanguageCode_RuleSignature",
                table: "LegendLanguageDiscourseReferenceRules",
                columns: new[] { "LanguageCode", "RuleSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDiscourseReferenceRules_LanguageCode_SelectorSemanticSignature_MaturityState_IsProductionEligible_SupersededUtc",
                table: "LegendLanguageDiscourseReferenceRules",
                columns: new[] { "LanguageCode", "SelectorSemanticSignature", "MaturityState", "IsProductionEligible", "SupersededUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageDiscourseReferenceRuleEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageDiscourseReferenceRules");

            migrationBuilder.DropColumn(
                name: "ResolvedBindingsJson",
                table: "LegendFounderAiDiscourseTurns");
        }
    }
}
