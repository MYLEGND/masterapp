using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFounderCrossExampleSemanticRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DerivationEvaluatorVersion",
                table: "LegendSemanticTransitionEvidence",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FounderRelationshipSemanticSignature",
                table: "LegendSemanticTransitionEvidence",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SemanticExampleIdentity",
                table: "LegendCurriculumExamples",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegendFounderSemanticExampleRelationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelationIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RelationshipSemanticIdentity = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RelationshipSemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceCurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultCurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMeaningGraphSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResultMeaningGraphSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IndependentSourceIdentity = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    ContributionState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsHumanVerifiedSupport = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendFounderSemanticExampleRelationEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendFounderSemanticExampleRelationEvidence_LegendCurriculumExamples_ResultCurriculumExampleId",
                        column: x => x.ResultCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendFounderSemanticExampleRelationEvidence_LegendCurriculumExamples_SourceCurriculumExampleId",
                        column: x => x.SourceCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendFounderSemanticExampleRelationEvidence_LegendCurriculumFamilies_ResultCurriculumFamilyId",
                        column: x => x.ResultCurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendFounderSemanticExampleRelationEvidence_LegendCurriculumFamilies_SourceCurriculumFamilyId",
                        column: x => x.SourceCurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendSemanticTransitionEvidence_FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence",
                column: "FounderSemanticExampleRelationEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendSemTransEv_FounderRelationSource",
                table: "LegendSemanticTransitionEvidence",
                columns: new[] { "FounderRelationshipSemanticSignature", "SourceSemanticFrameSignature", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_SemanticExampleIdentity",
                table: "LegendCurriculumExamples",
                column: "SemanticExampleIdentity",
                unique: true,
                filter: "[SemanticExampleIdentity] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemanticExampleRelationEvidence_RelationIdentity",
                table: "LegendFounderSemanticExampleRelationEvidence",
                column: "RelationIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemanticExampleRelationEvidence_ResultCurriculumExampleId_SupersededUtc",
                table: "LegendFounderSemanticExampleRelationEvidence",
                columns: new[] { "ResultCurriculumExampleId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemanticExampleRelationEvidence_ResultCurriculumFamilyId",
                table: "LegendFounderSemanticExampleRelationEvidence",
                column: "ResultCurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemanticExampleRelationEvidence_SourceCurriculumExampleId_SupersededUtc",
                table: "LegendFounderSemanticExampleRelationEvidence",
                columns: new[] { "SourceCurriculumExampleId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemanticExampleRelationEvidence_SourceCurriculumFamilyId",
                table: "LegendFounderSemanticExampleRelationEvidence",
                column: "SourceCurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderSemRel_Maturity",
                table: "LegendFounderSemanticExampleRelationEvidence",
                columns: new[] { "RelationshipSemanticSignature", "SourceMeaningGraphSignature", "ResultMeaningGraphSignature", "ContributionState", "SupersededUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_LegendSemanticTransitionEvidence_LegendFounderSemanticExampleRelationEvidence_FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence",
                column: "FounderSemanticExampleRelationEvidenceId",
                principalTable: "LegendFounderSemanticExampleRelationEvidence",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegendSemanticTransitionEvidence_LegendFounderSemanticExampleRelationEvidence_FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropTable(
                name: "LegendFounderSemanticExampleRelationEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendSemanticTransitionEvidence_FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendSemTransEv_FounderRelationSource",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendCurriculumExamples_SemanticExampleIdentity",
                table: "LegendCurriculumExamples");

            migrationBuilder.DropColumn(
                name: "DerivationEvaluatorVersion",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropColumn(
                name: "FounderRelationshipSemanticSignature",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropColumn(
                name: "FounderSemanticExampleRelationEvidenceId",
                table: "LegendSemanticTransitionEvidence");

            migrationBuilder.DropColumn(
                name: "SemanticExampleIdentity",
                table: "LegendCurriculumExamples");
        }
    }
}
