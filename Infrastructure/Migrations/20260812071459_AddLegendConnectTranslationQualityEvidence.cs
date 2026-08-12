using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectTranslationQualityEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "LegendTranslationAlignments",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            // Preserve the existing provenance authority while adding the
            // missing directional-alignment projection. Provider observations
            // remain provider-derived; a consented live observation remains
            // distinct; existing human verification remains Founder-approved.
            if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
UPDATE [LegendTranslationAlignments]
SET [Provenance] = CASE
    WHEN [HumanVerified] = CAST(1 AS bit) OR [Provider] = N'FounderApproved' THEN N'FounderApproved'
    WHEN [QualityState] = N'ConsentedLive' THEN N'ConsentedLiveTranslation'
    ELSE N'ProviderDerived'
END;");
            }
            else
            {
                migrationBuilder.Sql(@"
UPDATE ""LegendTranslationAlignments""
SET ""Provenance"" = CASE
    WHEN ""HumanVerified"" = 1 OR ""Provider"" = 'FounderApproved' THEN 'FounderApproved'
    WHEN ""QualityState"" = 'ConsentedLive' THEN 'ConsentedLiveTranslation'
    ELSE 'ProviderDerived'
END;");
            }

            migrationBuilder.CreateTable(
                name: "LegendTranslationQualityEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservedAlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedAlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StructuralPatternId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContextRelationshipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Signal = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ResolutionState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvidenceIdentity = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByAlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationQualityEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendLanguageContextRelationships_ContextRelationshipId",
                        column: x => x.ContextRelationshipId,
                        principalTable: "LegendLanguageContextRelationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendLanguageStructuralPatterns_StructuralPatternId",
                        column: x => x.StructuralPatternId,
                        principalTable: "LegendLanguageStructuralPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendLanguageTextUnits_SourceTextUnitId",
                        column: x => x.SourceTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendLanguageTextUnits_TargetTextUnitId",
                        column: x => x.TargetTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendTranslationAlignments_ObservedAlignmentId",
                        column: x => x.ObservedAlignmentId,
                        principalTable: "LegendTranslationAlignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendTranslationQualityEvidence_LegendTranslationAlignments_RelatedAlignmentId",
                        column: x => x.RelatedAlignmentId,
                        principalTable: "LegendTranslationAlignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_ContextRelationshipId",
                table: "LegendTranslationQualityEvidence",
                column: "ContextRelationshipId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_EvidenceIdentity",
                table: "LegendTranslationQualityEvidence",
                column: "EvidenceIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_ObservedAlignmentId_ResolutionState_SupersededUtc",
                table: "LegendTranslationQualityEvidence",
                columns: new[] { "ObservedAlignmentId", "ResolutionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_PairKey_Signal_ResolutionState",
                table: "LegendTranslationQualityEvidence",
                columns: new[] { "PairKey", "Signal", "ResolutionState" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_RelatedAlignmentId",
                table: "LegendTranslationQualityEvidence",
                column: "RelatedAlignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_SourceTextUnitId",
                table: "LegendTranslationQualityEvidence",
                column: "SourceTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_StructuralPatternId",
                table: "LegendTranslationQualityEvidence",
                column: "StructuralPatternId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationQualityEvidence_TargetTextUnitId",
                table: "LegendTranslationQualityEvidence",
                column: "TargetTextUnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendTranslationQualityEvidence");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "LegendTranslationAlignments");
        }
    }
}
