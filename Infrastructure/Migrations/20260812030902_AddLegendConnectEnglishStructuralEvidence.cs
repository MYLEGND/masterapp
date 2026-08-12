using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectEnglishStructuralEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaselineComponentSignature",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ComparedComponentSignature",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "LegendLanguageLexemes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NormalizedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SurfaceForm = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageLexemes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageCompositionalAnchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LexemeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentStartTokenIndex = table.Column<int>(type: "int", nullable: true),
                    ComponentLength = table.Column<int>(type: "int", nullable: true),
                    CurriculumFamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurriculumExampleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    AnchorSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageCompositionalAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageCompositionalAnchors_LegendCurriculumExamples_CurriculumExampleId",
                        column: x => x.CurriculumExampleId,
                        principalTable: "LegendCurriculumExamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageCompositionalAnchors_LegendCurriculumFamilies_CurriculumFamilyId",
                        column: x => x.CurriculumFamilyId,
                        principalTable: "LegendCurriculumFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageCompositionalAnchors_LegendLanguageLexemes_LexemeId",
                        column: x => x.LexemeId,
                        principalTable: "LegendLanguageLexemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageCompositionalAnchors_LegendLanguageTextUnits_TextUnitId",
                        column: x => x.TextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageLexicalOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LexemeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenIndex = table.Column<int>(type: "int", nullable: false),
                    CharacterOffset = table.Column<int>(type: "int", nullable: false),
                    CharacterLength = table.Column<int>(type: "int", nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageLexicalOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageLexicalOccurrences_LegendLanguageLexemes_LexemeId",
                        column: x => x.LexemeId,
                        principalTable: "LegendLanguageLexemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageLexicalOccurrences_LegendLanguageTextUnits_TextUnitId",
                        column: x => x.TextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageLexicalRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLexemeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedLexemeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelationshipKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceTokenIndex = table.Column<int>(type: "int", nullable: false),
                    RelatedTokenIndex = table.Column<int>(type: "int", nullable: false),
                    ObservationCount = table.Column<int>(type: "int", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageLexicalRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageLexicalRelationships_LegendLanguageLexemes_RelatedLexemeId",
                        column: x => x.RelatedLexemeId,
                        principalTable: "LegendLanguageLexemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageLexicalRelationships_LegendLanguageLexemes_SourceLexemeId",
                        column: x => x.SourceLexemeId,
                        principalTable: "LegendLanguageLexemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageLexicalRelationships_LegendLanguageTextUnits_TextUnitId",
                        column: x => x.TextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_CurriculumExampleId_AnchorSignature",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "CurriculumExampleId", "AnchorSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_CurriculumFamilyId",
                table: "LegendLanguageCompositionalAnchors",
                column: "CurriculumFamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_LanguageCode_Dimension_Value_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "LanguageCode", "Dimension", "Value", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_LexemeId",
                table: "LegendLanguageCompositionalAnchors",
                column: "LexemeId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageCompositionalAnchors_TextUnitId_SupersededUtc",
                table: "LegendLanguageCompositionalAnchors",
                columns: new[] { "TextUnitId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexemes_LanguageCode_NormalizedHash",
                table: "LegendLanguageLexemes",
                columns: new[] { "LanguageCode", "NormalizedHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexicalOccurrences_LexemeId_SupersededUtc",
                table: "LegendLanguageLexicalOccurrences",
                columns: new[] { "LexemeId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexicalOccurrences_TextUnitId_TokenIndex",
                table: "LegendLanguageLexicalOccurrences",
                columns: new[] { "TextUnitId", "TokenIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexicalRelationships_RelatedLexemeId",
                table: "LegendLanguageLexicalRelationships",
                column: "RelatedLexemeId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexicalRelationships_SourceLexemeId_RelatedLexemeId_SupersededUtc",
                table: "LegendLanguageLexicalRelationships",
                columns: new[] { "SourceLexemeId", "RelatedLexemeId", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageLexicalRelationships_TextUnitId_SourceTokenIndex_RelatedTokenIndex",
                table: "LegendLanguageLexicalRelationships",
                columns: new[] { "TextUnitId", "SourceTokenIndex", "RelatedTokenIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageCompositionalAnchors");

            migrationBuilder.DropTable(
                name: "LegendLanguageLexicalOccurrences");

            migrationBuilder.DropTable(
                name: "LegendLanguageLexicalRelationships");

            migrationBuilder.DropTable(
                name: "LegendLanguageLexemes");

            migrationBuilder.DropColumn(
                name: "BaselineComponentSignature",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "ComparedComponentSignature",
                table: "LegendLanguageStructuralEvidence");
        }
    }
}
