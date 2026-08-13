using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectFounderEnglishStructuralPropositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.AddColumn<string>(
                name: "PropositionSignature",
                table: "LegendLanguageStructuralPatterns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Curriculum-family and example identifiers are provenance, not
            // a reusable proposition identity. Reconcile every active
            // evidence row into one canonical controlled proposition based
            // only on its explicit variation dimension/value pair. The old
            // pattern rows remain auditable as retired observations; no
            // corpus, alignment, or evidence row is deleted.
            migrationBuilder.Sql("""
                ;WITH EvidencePropositions AS
                (
                    SELECT
                        evidence.Id AS EvidenceId,
                        evidence.PairKey,
                        evidence.LanguageCode,
                        evidence.VariationDimension,
                        evidence.CurriculumFamilyId,
                        pattern.RealizationSignature,
                        CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                            'controlled-proposition|',
                            LOWER(evidence.VariationDimension), '|',
                            CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                      LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                 THEN LOWER(evidence.BaselineVariationValue)
                                 ELSE LOWER(evidence.ComparedVariationValue) END, '|',
                            CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                      LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                 THEN LOWER(evidence.ComparedVariationValue)
                                 ELSE LOWER(evidence.BaselineVariationValue) END) COLLATE Latin1_General_100_BIN2_UTF8)), 2) AS PropositionSignature,
                        MAX(CASE WHEN evidence.IsHumanVerifiedSupport = 1 OR evidence.Provenance = 'FounderApproved'
                                 THEN 1 ELSE 0 END) OVER
                            (PARTITION BY evidence.PairKey, evidence.LanguageCode, evidence.VariationDimension,
                                CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                                    'controlled-proposition|',
                                    LOWER(evidence.VariationDimension), '|',
                                    CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                              LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                         THEN LOWER(evidence.BaselineVariationValue)
                                         ELSE LOWER(evidence.ComparedVariationValue) END, '|',
                                    CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                              LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                         THEN LOWER(evidence.ComparedVariationValue)
                                         ELSE LOWER(evidence.BaselineVariationValue) END) COLLATE Latin1_General_100_BIN2_UTF8)), 2)) AS HasFounderSupport,
                        ROW_NUMBER() OVER
                            (PARTITION BY evidence.PairKey, evidence.LanguageCode, evidence.VariationDimension,
                                CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                                    'controlled-proposition|',
                                    LOWER(evidence.VariationDimension), '|',
                                    CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                              LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                         THEN LOWER(evidence.BaselineVariationValue)
                                         ELSE LOWER(evidence.ComparedVariationValue) END, '|',
                                    CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                              LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                         THEN LOWER(evidence.ComparedVariationValue)
                                         ELSE LOWER(evidence.BaselineVariationValue) END) COLLATE Latin1_General_100_BIN2_UTF8)), 2)
                             ORDER BY evidence.CreatedUtc, evidence.Id) AS PropositionRank
                    FROM dbo.LegendLanguageStructuralEvidence AS evidence
                    INNER JOIN dbo.LegendLanguageStructuralPatterns AS pattern
                        ON pattern.Id = evidence.StructuralPatternId
                    WHERE evidence.SupersededUtc IS NULL
                )
                SELECT
                    NEWID() AS NewPatternId,
                    PairKey,
                    LanguageCode,
                    VariationDimension,
                    PropositionSignature,
                    CurriculumFamilyId,
                    RealizationSignature,
                    HasFounderSupport
                INTO #LegendStructuralPropositions
                FROM EvidencePropositions
                WHERE PropositionRank = 1;

                EXEC(N'
                    UPDATE dbo.LegendLanguageStructuralPatterns
                    SET PropositionSignature = CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                            CONCAT(''retired-pattern|'', CONVERT(varchar(36), Id))), 2),
                        MaturityState = ''Superseded'',
                        IsProductionEligible = 0,
                        SupersededUtc = COALESCE(SupersededUtc, SYSUTCDATETIME()),
                        UpdatedUtc = SYSUTCDATETIME();

                    INSERT INTO dbo.LegendLanguageStructuralPatterns
                    (
                        Id, PropositionSignature, CurriculumFamilyId, PairKey, LanguageCode,
                        VariationDimension, RealizationSignature, MaturityState,
                        SupportCount, ContradictionCount, IndependentSourceCount,
                        HumanVerifiedSupportCount, ProviderOnlySupportCount, Confidence,
                        IsProductionEligible, Provenance, SupersededUtc, CreatedUtc, UpdatedUtc
                    )
                    SELECT
                        NewPatternId, PropositionSignature, CurriculumFamilyId, PairKey, LanguageCode,
                        VariationDimension, RealizationSignature, ''Observation'',
                        0, 0, 0, 0, 0, CONVERT(decimal(5,4), 0),
                        0, CASE WHEN HasFounderSupport = 1 THEN ''FounderApproved'' ELSE ''ProviderDerived'' END,
                        NULL, SYSUTCDATETIME(), SYSUTCDATETIME()
                    FROM #LegendStructuralPropositions;
                ');

                ;WITH EvidencePropositions AS
                (
                    SELECT
                        evidence.Id,
                        evidence.PairKey,
                        evidence.LanguageCode,
                        evidence.VariationDimension,
                        CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                            'controlled-proposition|',
                            LOWER(evidence.VariationDimension), '|',
                            CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                      LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                 THEN LOWER(evidence.BaselineVariationValue)
                                 ELSE LOWER(evidence.ComparedVariationValue) END, '|',
                            CASE WHEN LOWER(evidence.BaselineVariationValue) COLLATE Latin1_General_100_BIN2 <=
                                      LOWER(evidence.ComparedVariationValue) COLLATE Latin1_General_100_BIN2
                                 THEN LOWER(evidence.ComparedVariationValue)
                                 ELSE LOWER(evidence.BaselineVariationValue) END) COLLATE Latin1_General_100_BIN2_UTF8)), 2) AS PropositionSignature
                    FROM dbo.LegendLanguageStructuralEvidence AS evidence
                    WHERE evidence.SupersededUtc IS NULL
                )
                UPDATE evidence
                SET StructuralPatternId = proposition.NewPatternId,
                    EvidenceSignature = source.PropositionSignature
                FROM dbo.LegendLanguageStructuralEvidence AS evidence
                INNER JOIN EvidencePropositions AS source ON source.Id = evidence.Id
                INNER JOIN #LegendStructuralPropositions AS proposition
                    ON proposition.PairKey = source.PairKey
                    AND proposition.LanguageCode = source.LanguageCode
                    AND proposition.VariationDimension = source.VariationDimension
                    AND proposition.PropositionSignature = source.PropositionSignature;

                EXEC(N'
                    UPDATE dbo.LegendLanguageStructuralPatterns
                    SET PropositionSignature = CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                            CONCAT(''orphan-pattern|'', CONVERT(varchar(36), Id))), 2),
                        MaturityState = ''Superseded'',
                        IsProductionEligible = 0,
                        SupersededUtc = COALESCE(SupersededUtc, SYSUTCDATETIME()),
                        UpdatedUtc = SYSUTCDATETIME()
                    WHERE PropositionSignature IS NULL;
                ');

                DROP TABLE #LegendStructuralPropositions;
                """);

            migrationBuilder.Sql("""
                EXEC(N'ALTER TABLE dbo.LegendLanguageStructuralPatterns
                    ALTER COLUMN PropositionSignature nvarchar(64) NOT NULL;');
                EXEC(N'CREATE INDEX IX_LegendLanguageStructuralPatterns_CurriculumFamilyId
                    ON dbo.LegendLanguageStructuralPatterns (CurriculumFamilyId);');
                EXEC(N'CREATE UNIQUE INDEX IX_LegendLanguageStructuralPatterns_PairKey_LanguageCode_VariationDimension_PropositionSignature
                    ON dbo.LegendLanguageStructuralPatterns (PairKey, LanguageCode, VariationDimension, PropositionSignature);');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IX_LegendLanguageStructuralPatterns_CurriculumFamilyId
                    ON dbo.LegendLanguageStructuralPatterns;
                DROP INDEX IX_LegendLanguageStructuralPatterns_PairKey_LanguageCode_VariationDimension_PropositionSignature
                    ON dbo.LegendLanguageStructuralPatterns;
                """);

            migrationBuilder.DropColumn(
                name: "PropositionSignature",
                table: "LegendLanguageStructuralPatterns");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageStructuralPatterns_CurriculumFamilyId_PairKey_LanguageCode_VariationDimension_RealizationSignature",
                table: "LegendLanguageStructuralPatterns",
                columns: new[] { "CurriculumFamilyId", "PairKey", "LanguageCode", "VariationDimension", "RealizationSignature" },
                unique: true);
        }
    }
}
