using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFounderMeaningGraphFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendLanguageMeaningNodeEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompositionalAnchorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SemanticDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SemanticValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ClauseKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageMeaningNodeEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningNodeEvidence_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningNodeEvidence_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningNodeEvidence_LegendLanguageCompositionalAnchors_CompositionalAnchorId",
                        column: x => x.CompositionalAnchorId,
                        principalTable: "LegendLanguageCompositionalAnchors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageMeaningRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RelationSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RelationKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SourceSemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetSemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClauseKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
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
                    table.PrimaryKey("PK_LegendLanguageMeaningRelations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageMeaningRelationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeaningRelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMeaningNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetMeaningNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_LegendLanguageMeaningRelationEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningRelationEvidence_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningRelationEvidence_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningRelationEvidence_LegendLanguageMeaningNodeEvidence_SourceMeaningNodeId",
                        column: x => x.SourceMeaningNodeId,
                        principalTable: "LegendLanguageMeaningNodeEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningRelationEvidence_LegendLanguageMeaningNodeEvidence_TargetMeaningNodeId",
                        column: x => x.TargetMeaningNodeId,
                        principalTable: "LegendLanguageMeaningNodeEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageMeaningRelationEvidence_LegendLanguageMeaningRelations_MeaningRelationId",
                        column: x => x.MeaningRelationId,
                        principalTable: "LegendLanguageMeaningRelations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_CompositionalAnchorId_SupersededUtc",
                table: "LegendLanguageMeaningNodeEvidence",
                columns: new[] { "CompositionalAnchorId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_CurriculumExampleId_NodeKey",
                table: "LegendLanguageMeaningNodeEvidence",
                columns: new[] { "CurriculumExampleId", "NodeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningNodeEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningNodeEvidence_LanguageCode_SemanticSignature_SupersededUtc",
                table: "LegendLanguageMeaningNodeEvidence",
                columns: new[] { "LanguageCode", "SemanticSignature", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_CurriculumExampleId_SupersededUtc",
                table: "LegendLanguageMeaningRelationEvidence",
                columns: new[] { "CurriculumExampleId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_CurriculumFamilyId",
                table: "LegendLanguageMeaningRelationEvidence",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_EvidenceIdentity",
                table: "LegendLanguageMeaningRelationEvidence",
                column: "EvidenceIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_MeaningRelationId_ContributionState_SupersededUtc",
                table: "LegendLanguageMeaningRelationEvidence",
                columns: new[] { "MeaningRelationId", "ContributionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_SourceMeaningNodeId",
                table: "LegendLanguageMeaningRelationEvidence",
                column: "SourceMeaningNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelationEvidence_TargetMeaningNodeId",
                table: "LegendLanguageMeaningRelationEvidence",
                column: "TargetMeaningNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelations_LanguageCode_MaturityState_IsProductionEligible_SupersededUtc",
                table: "LegendLanguageMeaningRelations",
                columns: new[] { "LanguageCode", "MaturityState", "IsProductionEligible", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageMeaningRelations_LanguageCode_RelationSignature",
                table: "LegendLanguageMeaningRelations",
                columns: new[] { "LanguageCode", "RelationSignature" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageMeaningRelationEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageMeaningNodeEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageMeaningRelations");
        }
    }
}
