using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendContextRelationshipCanonicalLookupCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LegendContextRelationships_ActiveCanonicalLookup",
                table: "LegendLanguageContextRelationships",
                columns: new[] { "CanonicalPairKey", "SourceTextUnitId", "RelatedTextUnitId", "RelationshipKind", "ContextSignature", "SupersededUtc" })
                .Annotation("SqlServer:Include", new[] { "Confidence", "ContextCategory", "CreatedUtc", "ObservationCount", "PairKey", "Provenance", "QualityState", "RegionalVariant", "SourcePatternSignature", "UpdatedUtc", "UsageRegister" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendContextRelationships_ActiveCanonicalLookup",
                table: "LegendLanguageContextRelationships");
        }
    }
}
