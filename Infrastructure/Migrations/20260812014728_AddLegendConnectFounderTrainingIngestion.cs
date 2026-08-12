using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectFounderTrainingIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededUtc",
                table: "LegendLanguageStructuralPatterns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededUtc",
                table: "LegendLanguageStructuralEvidence",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededUtc",
                table: "LegendLanguageContextRelationships",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededUtc",
                table: "LegendCurriculumExamples",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegendFounderTrainingSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FounderUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RawText = table.Column<string>(type: "nvarchar(max)", maxLength: 30000, nullable: false),
                    RawTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContextCategory = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UsageRegister = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    RegionalVariant = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    LegacySourceTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RawCharacterCount = table.Column<int>(type: "int", nullable: false),
                    AtomicUnitCount = table.Column<int>(type: "int", nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendFounderTrainingSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendFounderTrainingSubmissions_LegendLanguageTextUnits_LegacySourceTextUnitId",
                        column: x => x.LegacySourceTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendFounderTrainingSubmissionUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    ParagraphNumber = table.Column<int>(type: "int", nullable: false),
                    UnitType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendFounderTrainingSubmissionUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendFounderTrainingSubmissionUnits_LegendFounderTrainingSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "LegendFounderTrainingSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendFounderTrainingSubmissionUnits_LegendLanguageTextUnits_TextUnitId",
                        column: x => x.TextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_SupersededUtc",
                table: "LegendTranslationAlignments",
                column: "SupersededUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_SupersededUtc",
                table: "LegendLanguageStructuralPatterns",
                column: "SupersededUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_SupersededUtc",
                table: "LegendLanguageStructuralEvidence",
                column: "SupersededUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_SupersededUtc",
                table: "LegendLanguageContextRelationships",
                column: "SupersededUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumExamples_SupersededUtc",
                table: "LegendCurriculumExamples",
                column: "SupersededUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissions_LegacySourceTextUnitId",
                table: "LegendFounderTrainingSubmissions",
                column: "LegacySourceTextUnitId",
                unique: true,
                filter: "[LegacySourceTextUnitId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissions_ProcessingState_CreatedUtc",
                table: "LegendFounderTrainingSubmissions",
                columns: new[] { "ProcessingState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissions_SourceLanguageCode_RawTextHash",
                table: "LegendFounderTrainingSubmissions",
                columns: new[] { "SourceLanguageCode", "RawTextHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissionUnits_SubmissionId_SequenceNumber",
                table: "LegendFounderTrainingSubmissionUnits",
                columns: new[] { "SubmissionId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissionUnits_SubmissionId_TextUnitId",
                table: "LegendFounderTrainingSubmissionUnits",
                columns: new[] { "SubmissionId", "TextUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderTrainingSubmissionUnits_TextUnitId",
                table: "LegendFounderTrainingSubmissionUnits",
                column: "TextUnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendFounderTrainingSubmissionUnits");

            migrationBuilder.DropTable(
                name: "LegendFounderTrainingSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_SupersededUtc",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_SupersededUtc",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_SupersededUtc",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageContextRelationships_SupersededUtc",
                table: "LegendLanguageContextRelationships");

            migrationBuilder.DropIndex(
                name: "IX_LegendCurriculumExamples_SupersededUtc",
                table: "LegendCurriculumExamples");

            migrationBuilder.DropColumn(
                name: "SupersededUtc",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.DropColumn(
                name: "SupersededUtc",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "SupersededUtc",
                table: "LegendLanguageContextRelationships");

            migrationBuilder.DropColumn(
                name: "SupersededUtc",
                table: "LegendCurriculumExamples");
        }
    }
}
