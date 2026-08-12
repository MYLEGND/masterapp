using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectLanguageIntelligencePhase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurriculumFamilyId",
                table: "LegendCorpusCandidates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceCurriculumExampleId",
                table: "LegendCorpusCandidates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderPreferredLanguage",
                table: "InternalMessages",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegendCurriculumFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SemanticCategory = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCurriculumFamilies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendCurriculumExamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DerivedFromCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCurriculumExamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendCurriculumExamples_LegendCurriculumExamples_DerivedFromCurriculumExampleId",
                        column: x => x.DerivedFromCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendCurriculumExamples_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendCurriculumExamples_LegendLanguageTextUnits_TextUnitId",
                        column: x => x.TextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageStructuralPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VariationDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RealizationSignature = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MaturityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportCount = table.Column<int>(type: "int", nullable: false),
                    ContradictionCount = table.Column<int>(type: "int", nullable: false),
                    IsProductionEligible = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageStructuralPatterns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageStructuralPatterns_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendCurriculumExampleVariations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCurriculumExampleVariations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendCurriculumExampleVariations_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageStructuralEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StructuralPatternId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VariationDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BaselineCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComparedCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaselineVariationValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ComparedVariationValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    EvidenceSignature = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageStructuralEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageStructuralEvidence_LegendCurriculumExamples_BaselineCurriculumExampleId",
                        column: x => x.BaselineCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageStructuralEvidence_LegendCurriculumExamples_ComparedCurriculumExampleId",
                        column: x => x.ComparedCurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageStructuralEvidence_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageStructuralEvidence_LegendLanguageStructuralPatterns_StructuralPatternId",
                        column: x => x.StructuralPatternId,
                        principalTable: "LegendLanguageStructuralPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_CurriculumFamilyId_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates",
                columns: new[] { "CurriculumFamilyId", "SourceCurriculumExampleId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates",
                column: "SourceCurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_CurriculumFamilyId_LanguageCode_UpdatedUtc",
                table: "LegendCurriculumExamples",
                columns: new[] { "CurriculumFamilyId", "LanguageCode", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_CurriculumFamilyId_TextUnitId",
                table: "LegendCurriculumExamples",
                columns: new[] { "CurriculumFamilyId", "TextUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_DerivedFromCurriculumExampleId",
                table: "LegendCurriculumExamples",
                column: "DerivedFromCurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_TextUnitId",
                table: "LegendCurriculumExamples",
                column: "TextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExampleVariations_CurriculumExampleId_Dimension",
                table: "LegendCurriculumExampleVariations",
                columns: new[] { "CurriculumExampleId", "Dimension" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumFamilies_FamilyKey",
                table: "LegendCurriculumFamilies",
                column: "FamilyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_BaselineCurriculumExampleId",
                table: "LegendLanguageStructuralEvidence",
                column: "BaselineCurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_ComparedCurriculumExampleId",
                table: "LegendLanguageStructuralEvidence",
                column: "ComparedCurriculumExampleId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_CurriculumFamilyId_LanguageCode_VariationDimension_BaselineCurriculumExampleId_ComparedCurr~",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "CurriculumFamilyId", "LanguageCode", "VariationDimension", "BaselineCurriculumExampleId", "ComparedCurriculumExampleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralPatternId_CreatedUtc",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "StructuralPatternId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "CurriculumFamilyId", "LanguageCode", "VariationDimension", "RealizationSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "LanguageCode", "MaturityState", "IsProductionEligible" });

            migrationBuilder.AddForeignKey(
                name: "FK_LegendCorpusCandidates_LegendCurriculumExamples_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates",
                column: "SourceCurriculumExampleId",
                principalTable: "LegendCurriculumExamples",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LegendCorpusCandidates_LegendCurriculumFamilies_CurriculumFamilyId",
                table: "LegendCorpusCandidates",
                column: "CurriculumFamilyId",
                principalTable: "LegendCurriculumFamilies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegendCorpusCandidates_LegendCurriculumExamples_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropForeignKey(
                name: "FK_LegendCorpusCandidates_LegendCurriculumFamilies_CurriculumFamilyId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropTable(
                name: "LegendCurriculumExampleVariations");

            migrationBuilder.DropTable(
                name: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropTable(
                name: "LegendCurriculumExamples");

            migrationBuilder.DropTable(
                name: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropTable(
                name: "LegendCurriculumFamilies");

            migrationBuilder.DropIndex(
                name: "IX_LegendCorpusCandidates_CurriculumFamilyId_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropIndex(
                name: "IX_LegendCorpusCandidates_SourceCurriculumExampleId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "CurriculumFamilyId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "SourceCurriculumExampleId",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "SenderPreferredLanguage",
                table: "InternalMessages");
        }
    }
}
