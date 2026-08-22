using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendReusableMeaningPrimitives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendLanguageMeaningPrimitives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SemanticDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SemanticValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    MaturityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportCount = table.Column<int>(type: "int", nullable: false),
                    ContradictionCount = table.Column<int>(type: "int", nullable: false),
                    IndependentSourceCount = table.Column<int>(type: "int", nullable: false),
                    HumanVerifiedSupportCount = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    IsProductionEligible = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageMeaningPrimitives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageMeaningPrimitiveEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeaningPrimitiveId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeaningNodeEvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_LegendLanguageMeaningPrimitiveEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningPrimitiveEvidence_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningPrimitiveEvidence_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningPrimitiveEvidence_LegendLanguageMeaningNodeEvidence_MeaningNodeEvidenceId",
                        column: x => x.MeaningNodeEvidenceId,
                        principalTable: "LegendLanguageMeaningNodeEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningPrimitiveEvidence_LegendLanguageMeaningPrimitives_MeaningPrimitiveId",
                        column: x => x.MeaningPrimitiveId,
                        principalTable: "LegendLanguageMeaningPrimitives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_CurriculumExampleId",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                column: "CurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_EvidenceIdentity",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                column: "EvidenceIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_MeaningNodeEvidenceId",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                column: "MeaningNodeEvidenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitiveEvidence_MeaningPrimitiveId_ContributionState_SupersededUtc",
                table: "LegendLanguageMeaningPrimitiveEvidence",
                columns: new[] { "MeaningPrimitiveId", "ContributionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitives_LanguageCode_MaturityState_IsProductionEligible_SupersededUtc",
                table: "LegendLanguageMeaningPrimitives",
                columns: new[] { "LanguageCode", "MaturityState", "IsProductionEligible", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningPrimitives_LanguageCode_SemanticSignature",
                table: "LegendLanguageMeaningPrimitives",
                columns: new[] { "LanguageCode", "SemanticSignature" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageMeaningPrimitiveEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageMeaningPrimitives");
        }
    }
}
