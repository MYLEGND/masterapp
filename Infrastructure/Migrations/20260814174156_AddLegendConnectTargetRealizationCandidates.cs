using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectTargetRealizationCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PairKey",
                table: "LegendLanguageCompositionalAnchors",
                type: "nvarchar(72)",
                maxLength: 72,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_PairKey_SemanticSignature_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "PairKey", "SemanticSignature", "SupersededUtc" });

            migrationBuilder.CreateTable(
                name: "LegendLanguageTargetRealizationCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SemanticSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VariationDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SemanticValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TargetRealization = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ContextSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TemplateSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SlotSignature = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CandidateIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VerificationState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MaturityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportCount = table.Column<int>(type: "int", nullable: false),
                    IndependentSourceCount = table.Column<int>(type: "int", nullable: false),
                    HumanVerifiedSupportCount = table.Column<int>(type: "int", nullable: false),
                    ProviderOnlySupportCount = table.Column<int>(type: "int", nullable: false),
                    ContradictionCount = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    IsProductionEligible = table.Column<bool>(type: "bit", nullable: false),
                    VerifiedAnchorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VerifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VerifiedByFounderUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RejectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedByFounderUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageTargetRealizationCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationCandidates_LegendLanguageCompositionalAnchors_VerifiedAnchorId",
                        column: x => x.VerifiedAnchorId,
                        principalTable: "LegendLanguageCompositionalAnchors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageTargetRealizationEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetStartTokenIndex = table.Column<int>(type: "int", nullable: false),
                    TargetTokenLength = table.Column<int>(type: "int", nullable: false),
                    EvidenceIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsHumanVerifiedSupport = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageTargetRealizationEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendCurriculumExamples_SourceCurriculumExampleId",
                        column: x => x.SourceCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendCurriculumExamples_TargetCurriculumExampleId",
                        column: x => x.TargetCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendLanguageTargetRealizationCandidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "LegendLanguageTargetRealizationCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendLanguageTextUnits_SourceTextUnitId",
                        column: x => x.SourceTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendLanguageTextUnits_TargetTextUnitId",
                        column: x => x.TargetTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTargetRealizationEvidence_LegendTranslationAlignments_SourceAlignmentId",
                        column: x => x.SourceAlignmentId,
                        principalTable: "LegendTranslationAlignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationCandidates_CandidateIdentity",
                table: "LegendLanguageTargetRealizationCandidates",
                column: "CandidateIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationCandidates_PairKey_SemanticSignature_ContextSignature_SupersededUtc",
                table: "LegendLanguageTargetRealizationCandidates",
                columns: new[] { "PairKey", "SemanticSignature", "ContextSignature", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationCandidates_VerificationState_MaturityState_SupersededUtc",
                table: "LegendLanguageTargetRealizationCandidates",
                columns: new[] { "VerificationState", "MaturityState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationCandidates_VerifiedAnchorId",
                table: "LegendLanguageTargetRealizationCandidates",
                column: "VerifiedAnchorId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_CandidateId_EvidenceIdentity",
                table: "LegendLanguageTargetRealizationEvidence",
                columns: new[] { "CandidateId", "EvidenceIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_SourceAlignmentId_SupersededUtc",
                table: "LegendLanguageTargetRealizationEvidence",
                columns: new[] { "SourceAlignmentId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_SourceCurriculumExampleId",
                table: "LegendLanguageTargetRealizationEvidence",
                column: "SourceCurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_SourceTextUnitId",
                table: "LegendLanguageTargetRealizationEvidence",
                column: "SourceTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_TargetCurriculumExampleId_SupersededUtc",
                table: "LegendLanguageTargetRealizationEvidence",
                columns: new[] { "TargetCurriculumExampleId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTargetRealizationEvidence_TargetTextUnitId",
                table: "LegendLanguageTargetRealizationEvidence",
                column: "TargetTextUnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageTargetRealizationEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageTargetRealizationCandidates");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageCompositionalAnchors_PairKey_SemanticSignature_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors");

            migrationBuilder.DropColumn(
                name: "PairKey",
                table: "LegendLanguageCompositionalAnchors");
        }
    }
}
