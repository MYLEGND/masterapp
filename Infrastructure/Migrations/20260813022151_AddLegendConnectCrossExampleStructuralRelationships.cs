using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectCrossExampleStructuralRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StructuralRelationshipContributionState",
                table: "LegendLanguageStructuralEvidence",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StructuralRelationshipId",
                table: "LegendLanguageStructuralEvidence",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegendLanguageStructuralRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VariationDimension = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RelationshipSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AnchorLayoutSignature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MaturityState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportCount = table.Column<int>(type: "int", nullable: false),
                    ContradictionCount = table.Column<int>(type: "int", nullable: false),
                    IndependentSourceCount = table.Column<int>(type: "int", nullable: false),
                    HumanVerifiedSupportCount = table.Column<int>(type: "int", nullable: false),
                    ProviderOnlySupportCount = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    IsProductionEligible = table.Column<bool>(type: "bit", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageStructuralRelationships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralRelationshipId_StructuralRelationshipContributionState_SupersededUtc",
                table: "LegendLanguageStructuralEvidence",
                columns: new[] { "StructuralRelationshipId", "StructuralRelationshipContributionState", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralRelationships_PairKey_LanguageCode_MaturityState_IsProductionEligible",
                table: "LegendLanguageStructuralRelationships",
                columns: new[] { "PairKey", "LanguageCode", "MaturityState", "IsProductionEligible" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralRelationships_PairKey_LanguageCode_VariationDimension_RelationshipSignature",
                table: "LegendLanguageStructuralRelationships",
                columns: new[] { "PairKey", "LanguageCode", "VariationDimension", "RelationshipSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralRelationships_SupersededUtc",
                table: "LegendLanguageStructuralRelationships",
                column: "SupersededUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_LegendLanguageStructuralEvidence_LegendLanguageStructuralRelationships_StructuralRelationshipId",
                table: "LegendLanguageStructuralEvidence",
                column: "StructuralRelationshipId",
                principalTable: "LegendLanguageStructuralRelationships",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LegendLanguageStructuralEvidence_LegendLanguageStructuralRelationships_StructuralRelationshipId",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropTable(
                name: "LegendLanguageStructuralRelationships");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralEvidence_StructuralRelationshipId_StructuralRelationshipContributionState_SupersededUtc",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "StructuralRelationshipContributionState",
                table: "LegendLanguageStructuralEvidence");

            migrationBuilder.DropColumn(
                name: "StructuralRelationshipId",
                table: "LegendLanguageStructuralEvidence");
        }
    }
}
