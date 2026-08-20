using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Adds the single durable evidence relation required to govern reusable
/// source-semantic-frame to result-semantic-frame transformations. It stores
/// no chat prompts, answers, or surface realization templates.
/// </summary>
public partial class AddLegendSemanticTransitionEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LegendSemanticTransitionEvidence",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TransitionSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SourceSemanticFrameSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResultSemanticFrameSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SourceSemanticFrame = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                ResultSemanticFrame = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ResultLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SourceCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ResultCurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                table.PrimaryKey("PK_LegendSemanticTransitionEvidence", x => x.Id);
                table.ForeignKey(
                    name: "FK_LegendSemanticTransitionEvidence_LegendCurriculumExamples_ResultCurriculumExampleId",
                    column: x => x.ResultCurriculumExampleId,
                    principalTable: "LegendCurriculumExamples",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LegendSemanticTransitionEvidence_LegendCurriculumExamples_SourceCurriculumExampleId",
                    column: x => x.SourceCurriculumExampleId,
                    principalTable: "LegendCurriculumExamples",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LegendSemanticTransitionEvidence_ResultCurriculumExampleId_SupersededUtc",
            table: "LegendSemanticTransitionEvidence",
            columns: new[] { "ResultCurriculumExampleId", "SupersededUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_LegendSemanticTransitionEvidence_SourceCurriculumExampleId_SupersededUtc",
            table: "LegendSemanticTransitionEvidence",
            columns: new[] { "SourceCurriculumExampleId", "SupersededUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_LegendSemTransEv_FrameLang",
            table: "LegendSemanticTransitionEvidence",
            columns: new[] { "SourceSemanticFrameSignature", "ResultSemanticFrameSignature", "SourceLanguageCode", "ResultLanguageCode", "SupersededUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_LegendSemanticTransitionEvidence_TransitionSignature_SourceCurriculumExampleId_ResultCurriculumExampleId",
            table: "LegendSemanticTransitionEvidence",
            columns: new[] { "TransitionSignature", "SourceCurriculumExampleId", "ResultCurriculumExampleId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LegendSemanticTransitionEvidence");
}
