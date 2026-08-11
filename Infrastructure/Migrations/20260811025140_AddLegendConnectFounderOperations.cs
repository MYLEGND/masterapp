using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectFounderOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AzureFallbackCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ContextualCompositionObservationCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ContextCategory",
                table: "LegendTranslationLearningEvents",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByAlignmentId",
                table: "LegendTranslationAlignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededUtc",
                table: "LegendTranslationAlignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegendConnectKnowledgeAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FounderUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: true),
                    TextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededAlignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectKnowledgeAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendConnectOperationalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectOperationalEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageContextRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: true),
                    SourceTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedTextUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelationshipKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ContextSignature = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    SourcePatternSignature = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContextCategory = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UsageRegister = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    RegionalVariant = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    QualityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ObservationCount = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageContextRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageContextRelationships_LegendLanguageTextUnits_RelatedTextUnitId",
                        column: x => x.RelatedTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegendLanguageContextRelationships_LegendLanguageTextUnits_SourceTextUnitId",
                        column: x => x.SourceTextUnitId,
                        principalTable: "LegendLanguageTextUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationSystemUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsageDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SameLanguageBypassCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationSystemUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_SupersededByAlignmentId",
                table: "LegendTranslationAlignments",
                column: "SupersededByAlignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectKnowledgeAuditEntries_FounderUserId_OccurredUtc",
                table: "LegendConnectKnowledgeAuditEntries",
                columns: new[] { "FounderUserId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectKnowledgeAuditEntries_LanguageCode_OccurredUtc",
                table: "LegendConnectKnowledgeAuditEntries",
                columns: new[] { "LanguageCode", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectKnowledgeAuditEntries_PairKey_OccurredUtc",
                table: "LegendConnectKnowledgeAuditEntries",
                columns: new[] { "PairKey", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectOperationalEvents_LanguageCode_OccurredUtc",
                table: "LegendConnectOperationalEvents",
                columns: new[] { "LanguageCode", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectOperationalEvents_PairKey_OccurredUtc",
                table: "LegendConnectOperationalEvents",
                columns: new[] { "PairKey", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectOperationalEvents_Severity_IsResolved_OccurredUtc",
                table: "LegendConnectOperationalEvents",
                columns: new[] { "Severity", "IsResolved", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_PairKey_SourcePatternSignature_QualityState",
                table: "LegendLanguageContextRelationships",
                columns: new[] { "PairKey", "SourcePatternSignature", "QualityState" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_PairKey_SourceTextUnitId_RelatedTextUnitId_RelationshipKind_ContextSignature",
                table: "LegendLanguageContextRelationships",
                columns: new[] { "PairKey", "SourceTextUnitId", "RelatedTextUnitId", "RelationshipKind", "ContextSignature" },
                unique: true,
                filter: "[PairKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_RelatedTextUnitId",
                table: "LegendLanguageContextRelationships",
                column: "RelatedTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_SourceTextUnitId",
                table: "LegendLanguageContextRelationships",
                column: "SourceTextUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationSystemUsages_UsageDate",
                table: "LegendTranslationSystemUsages",
                column: "UsageDate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendConnectKnowledgeAuditEntries");

            migrationBuilder.DropTable(
                name: "LegendConnectOperationalEvents");

            migrationBuilder.DropTable(
                name: "LegendLanguageContextRelationships");

            migrationBuilder.DropTable(
                name: "LegendTranslationSystemUsages");

            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_SupersededByAlignmentId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "AzureFallbackCount",
                table: "LegendTranslationPairDemands");

            migrationBuilder.DropColumn(
                name: "ContextualCompositionObservationCount",
                table: "LegendTranslationPairDemands");

            migrationBuilder.DropColumn(
                name: "ContextCategory",
                table: "LegendTranslationLearningEvents");

            migrationBuilder.DropColumn(
                name: "SupersededByAlignmentId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "SupersededUtc",
                table: "LegendTranslationAlignments");
        }
    }
}
