using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeLegendContextRelationshipIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageContextRelationships_PairKey_SourceTextUnitId_RelatedTextUnitId_RelationshipKind_ContextSignature",
                table: "LegendLanguageContextRelationships");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalPairKey",
                table: "LegendLanguageContextRelationships",
                type: "nvarchar(72)",
                maxLength: 72,
                nullable: true,
                computedColumnSql: "COALESCE([PairKey], '')",
                stored: true);

            // The former nullable composite index permitted more than one
            // active row whenever PairKey was NULL.  Preserve every historic
            // row and its provenance, but supersede exact duplicate active
            // projections before the new active-identity constraint exists.
            // Evidence pointing at a superseded projection remains intact for
            // audit; only the one deterministic active canonical projection
            // participates in future reuse.
            migrationBuilder.Sql("""
                ;WITH ActiveDuplicates AS
                (
                    SELECT [Id],
                           ROW_NUMBER() OVER
                           (
                               PARTITION BY [CanonicalPairKey], [SourceTextUnitId], [RelatedTextUnitId],
                                            [RelationshipKind], [ContextSignature]
                               ORDER BY [CreatedUtc], [Id]
                           ) AS [DuplicateRank]
                    FROM [LegendLanguageContextRelationships]
                    WHERE [SupersededUtc] IS NULL
                )
                UPDATE [relationship]
                SET [SupersededUtc] = SYSUTCDATETIME(),
                    [UpdatedUtc] = SYSUTCDATETIME()
                FROM [LegendLanguageContextRelationships] AS [relationship]
                INNER JOIN [ActiveDuplicates] AS [duplicate]
                    ON [duplicate].[Id] = [relationship].[Id]
                WHERE [duplicate].[DuplicateRank] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_CanonicalIdentity",
                table: "LegendLanguageContextRelationships",
                columns: new[] { "CanonicalPairKey", "SourceTextUnitId", "RelatedTextUnitId", "RelationshipKind", "ContextSignature" },
                unique: true,
                filter: "[SupersededUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageContextRelationships_CanonicalIdentity",
                table: "LegendLanguageContextRelationships");

            migrationBuilder.DropColumn(
                name: "CanonicalPairKey",
                table: "LegendLanguageContextRelationships");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageContextRelationships_PairKey_SourceTextUnitId_RelatedTextUnitId_RelationshipKind_ContextSignature",
                table: "LegendLanguageContextRelationships",
                columns: new[] { "PairKey", "SourceTextUnitId", "RelatedTextUnitId", "RelationshipKind", "ContextSignature" },
                unique: true,
                filter: "[PairKey] IS NOT NULL");
        }
    }
}
