using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Messaging;

namespace Infrastructure.Messaging;

/// <summary>
/// The single source-authority and evidence-admissibility policy beneath the
/// existing governed reasoning executor. Search position, popularity,
/// repetition, domain age, and model confidence are deliberately absent from
/// every rule. Authority is evaluated for the exact claim subject and scope.
/// </summary>
internal static class LegendConnectResearchEvidenceAdmissibilityPolicy
{
    internal const string PolicyIdentity =
        LegendConnectResearchContracts.EvidenceAdmissibilityPolicy;

    private static readonly IReadOnlyDictionary<LegendConnectResearchClaimSubject, EvidenceStandard>
        Standards = new Dictionary<LegendConnectResearchClaimSubject, EvidenceStandard>
        {
            [LegendConnectResearchClaimSubject.General] = Standard(
                LegendConnectResearchClaimSubject.General,
                null,
                2,
                LegendConnectResearchAuthorityScope.GeneralRecord,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                    LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority,
                    LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport,
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                    LegendConnectResearchSourceClass.IndependentSecondaryAnalysis
                ],
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement
                ]),
            [LegendConnectResearchClaimSubject.Legal] = Standard(
                LegendConnectResearchClaimSubject.Legal,
                TimeSpan.FromDays(366),
                2,
                LegendConnectResearchAuthorityScope.ControllingLegalRecord,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                    LegendConnectResearchSourceClass.IndependentSecondaryAnalysis
                ],
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority
                ]),
            [LegendConnectResearchClaimSubject.Medical] = Standard(
                LegendConnectResearchClaimSubject.Medical,
                TimeSpan.FromDays(366 * 5),
                2,
                LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
                false,
                [
                    LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                    LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority
                ],
                []),
            [LegendConnectResearchClaimSubject.Scientific] = Standard(
                LegendConnectResearchClaimSubject.Scientific,
                TimeSpan.FromDays(366 * 10),
                2,
                LegendConnectResearchAuthorityScope.MedicalScientificEvidence,
                false,
                [
                    LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                    LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority
                ],
                []),
            [LegendConnectResearchClaimSubject.Financial] = Standard(
                LegendConnectResearchClaimSubject.Financial,
                TimeSpan.FromDays(400),
                2,
                LegendConnectResearchAuthorityScope.RegulatoryFinancialDisclosure,
                true,
                [
                    LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                    LegendConnectResearchSourceClass.IndependentSecondaryAnalysis
                ],
                [
                    LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority
                ]),
            [LegendConnectResearchClaimSubject.Security] = Standard(
                LegendConnectResearchClaimSubject.Security,
                TimeSpan.FromDays(90),
                2,
                LegendConnectResearchAuthorityScope.SecurityRecord,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                    LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting
                ],
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation
                ]),
            [LegendConnectResearchClaimSubject.CurrentEvent] = Standard(
                LegendConnectResearchClaimSubject.CurrentEvent,
                TimeSpan.FromDays(14),
                2,
                LegendConnectResearchAuthorityScope.CurrentEventRecord,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting
                ],
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement
                ]),
            [LegendConnectResearchClaimSubject.Product] = Standard(
                LegendConnectResearchClaimSubject.Product,
                TimeSpan.FromDays(730),
                2,
                LegendConnectResearchAuthorityScope.OfficialProductTechnicalDocumentation,
                true,
                [
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                    LegendConnectResearchSourceClass.IndependentSecondaryAnalysis
                ],
                [
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement
                ]),
            [LegendConnectResearchClaimSubject.Operational] = Standard(
                LegendConnectResearchClaimSubject.Operational,
                TimeSpan.FromDays(90),
                2,
                LegendConnectResearchAuthorityScope.OwnOperations,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting
                ],
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.FirstPartyCompanyStatement
                ]),
            [LegendConnectResearchClaimSubject.Historical] = Standard(
                LegendConnectResearchClaimSubject.Historical,
                null,
                2,
                LegendConnectResearchAuthorityScope.HistoricalRecord,
                true,
                [
                    LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                    LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority,
                    LegendConnectResearchSourceClass.PeerReviewedOriginalResearch,
                    LegendConnectResearchSourceClass.IndependentProfessionalReporting,
                    LegendConnectResearchSourceClass.IndependentSecondaryAnalysis
                ],
                [LegendConnectResearchSourceClass.PrimaryOfficialRecord])
        };

    internal static LegendConnectResearchEvidencePolicyAssessment Assess(
        IReadOnlyList<LegendConnectResearchSourceIdentity> sources,
        IReadOnlyList<LegendConnectRetrievedDocument> documents,
        IReadOnlyList<LegendConnectCitation> citations,
        IReadOnlyList<LegendConnectClaimEvidence> claims,
        IReadOnlyList<LegendConnectContradictingEvidence> contradictions,
        int minimumIndependentSources,
        DateTime assessedUtc,
        LegendConnectResearchLanguageLineage? languageLineage = null)
    {
        var boundedMinimum = Math.Clamp(minimumIndependentSources, 1, 3);
        assessedUtc = NormalizeUtc(assessedUtc);
        var sourceById = UniqueBy(sources, item => item.SourceIdentity);
        var documentById = UniqueBy(
            documents.Where(item => item.RetrievalSucceeded),
            item => item.DocumentIdentity);
        var citationById = UniqueBy(citations, item => item.CitationIdentity);
        var contentHashCounts = documentById.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.ContentHash))
            .GroupBy(item => item.ContentHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rows = claims.Select(item => EvidenceRow.From(item))
            .Concat(contradictions.Select(item => EvidenceRow.From(item)))
            .ToArray();
        var duplicateEvidenceIds = rows
            .Where(item => !string.IsNullOrWhiteSpace(item.EvidenceIdentity))
            .GroupBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var classificationConflictEvidenceIds = rows
            .GroupBy(item => NormalizeClaimKey(item.ClaimIdentity), StringComparer.Ordinal)
            .Where(group => group
                // Subject and required authority scope classify the claim.
                // Statement kind classifies each source relationship to that
                // claim, so firsthand testimony and an unrelated factual
                // assertion may legitimately receive different dispositions
                // without making the shared claim identity malformed.
                .Select(item => (item.Subject, item.RequiredAuthorityScope))
                .Distinct()
                .Count() != 1)
            .SelectMany(group => group.Select(item => item.EvidenceIdentity))
            .ToHashSet(StringComparer.Ordinal);
        var directRowsByClaimAndSource = rows
            .Where(item => item.Support == LegendConnectResearchEvidenceSupport.Direct)
            .GroupBy(item => (item.ClaimIdentity, item.SourceIdentity))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var evaluated = rows.Select(row => Evaluate(
                row,
                duplicateEvidenceIds,
                classificationConflictEvidenceIds,
                sourceById,
                documentById,
                citationById,
                contentHashCounts,
                directRowsByClaimAndSource,
                assessedUtc,
                languageLineage))
            .ToArray();

        var material = new List<LegendConnectResearchMaterialClaimEvidence>();
        var decisions = new List<LegendConnectResearchEvidenceAdmissibility>(evaluated.Length);
        foreach (var item in evaluated)
        {
            if (item.Decision.Disposition == LegendConnectResearchEvidenceDisposition.Rejected ||
                !sourceById.TryGetValue(item.Row.SourceIdentity, out var source) ||
                !documentById.TryGetValue(item.Row.DocumentIdentity, out var document) ||
                !citationById.TryGetValue(item.Row.CitationIdentity, out var citation))
            {
                decisions.Add(item.Decision);
                continue;
            }

            if (source.PublishedUtc is not { } publishedUtc)
            {
                decisions.Add(item.Decision with
                {
                    Disposition = LegendConnectResearchEvidenceDisposition.Rejected,
                    ReasonCode = "research_source_publication_timestamp_missing"
                });
                continue;
            }

            var evidence = Materialize(
                item,
                source,
                document,
                citation,
                publishedUtc,
                assessedUtc,
                languageLineage,
                boundedMinimum);
            material.Add(evidence);
            decisions.Add(item.Decision with
            {
                NormalizedClaimIdentity = evidence.NormalizedClaimIdentity,
                MaterialEvidenceIdentity = evidence.EvidenceIdentity
            });
        }

        return new(
            material.OrderBy(item => item.NormalizedClaimIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal)
                .ToArray(),
            decisions,
            boundedMinimum);
    }

    internal static EvidenceStandard StandardFor(LegendConnectResearchClaimSubject subject) =>
        Standards.TryGetValue(subject, out var standard)
            ? standard
            : Standards[LegendConnectResearchClaimSubject.General];

    private static EvaluatedEvidence Evaluate(
        EvidenceRow row,
        IReadOnlySet<string> duplicateEvidenceIds,
        IReadOnlySet<string> classificationConflictEvidenceIds,
        IReadOnlyDictionary<string, LegendConnectResearchSourceIdentity> sourceById,
        IReadOnlyDictionary<string, LegendConnectRetrievedDocument> documentById,
        IReadOnlyDictionary<string, LegendConnectCitation> citationById,
        IReadOnlyDictionary<string, int> contentHashCounts,
        IReadOnlyDictionary<(string ClaimIdentity, string SourceIdentity), EvidenceRow[]> directRows,
        DateTime assessedUtc,
        LegendConnectResearchLanguageLineage? languageLineage)
    {
        var standard = StandardFor(row.Subject);
        var sourceIdentity = row.SourceIdentity ?? string.Empty;
        var emptyLineage = "unresolved:" +
            (sourceIdentity.Length == 0 ? "missing" : sourceIdentity);

        EvaluatedEvidence Result(
            LegendConnectResearchEvidenceDisposition disposition,
            string reason,
            string lineage,
            bool preliminarilyAdmissible = false) =>
            new(
                row,
                new LegendConnectResearchEvidenceAdmissibility(
                    row.EvidenceIdentity,
                    row.ClaimIdentity,
                    sourceIdentity,
                    row.Subject,
                    sourceById.TryGetValue(sourceIdentity, out var typedSource)
                        ? typedSource.SourceClass
                        : LegendConnectResearchSourceClass.UnknownSource,
                    disposition,
                    reason,
                    lineage,
                    row.IsContradiction,
                    assessedUtc,
                    PolicyIdentity),
                preliminarilyAdmissible);

        if (string.IsNullOrWhiteSpace(row.EvidenceIdentity) ||
            string.IsNullOrWhiteSpace(row.ClaimIdentity) ||
            string.IsNullOrWhiteSpace(row.Statement) ||
            string.IsNullOrWhiteSpace(sourceIdentity) ||
            duplicateEvidenceIds.Contains(row.EvidenceIdentity) ||
            !sourceById.TryGetValue(sourceIdentity, out var source) ||
            !documentById.TryGetValue(row.DocumentIdentity, out var document) ||
            !citationById.TryGetValue(row.CitationIdentity, out var citation) ||
            !HasCompleteLineage(row, source, document, citation))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_claim_lineage_incomplete",
                emptyLineage);
        }

        if (classificationConflictEvidenceIds.Contains(row.EvidenceIdentity))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_claim_classification_conflict",
                emptyLineage);
        }

        var lineage = ResolveIndependentLineage(source, document, contentHashCounts);
        var premiseCount = row.PremiseClaimIdentities?.Count ?? 0;
        var invalidPremises = premiseCount > 3 ||
            (row.PremiseClaimIdentities ?? []).Any(identity =>
                string.IsNullOrWhiteSpace(identity) ||
                string.Equals(identity, row.ClaimIdentity, StringComparison.Ordinal));
        if (invalidPremises ||
            (row.StatementKind == LegendConnectResearchStatementKind.Inference &&
             (row.Support != LegendConnectResearchEvidenceSupport.Direct ||
              premiseCount is < 2 or > 3 ||
              string.IsNullOrWhiteSpace(row.DiscriminatingClaimIdentity))) ||
            (row.StatementKind != LegendConnectResearchStatementKind.Inference &&
             (premiseCount > 0 || !string.IsNullOrWhiteSpace(row.DiscriminatingClaimIdentity))))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_bounded_inference_proposal_invalid",
                lineage);
        }
        if (!string.IsNullOrWhiteSpace(row.CorrectsSourceIdentity) &&
            (!sourceById.ContainsKey(row.CorrectsSourceIdentity) ||
             string.Equals(row.CorrectsSourceIdentity, row.SourceIdentity, StringComparison.Ordinal)))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_correction_lineage_invalid",
                lineage);
        }
        if (row.ExtractionMethod != LegendConnectResearchExtractionMethod.ModelAssistedProposal ||
            !TryLocateExactPassage(
                row.SupportingExcerpt,
                document,
                out _))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                row.ExtractionMethod != LegendConnectResearchExtractionMethod.ModelAssistedProposal
                    ? "research_extraction_method_not_proposal"
                    : "research_claim_direct_support_missing",
                lineage);
        }

        if (row.Support == LegendConnectResearchEvidenceSupport.Direct)
        {
            var documentLanguage = document.DocumentLanguageCode ?? "und";
            var translated = !string.Equals(
                documentLanguage,
                row.EvidenceLanguageCode,
                StringComparison.OrdinalIgnoreCase);
            if (translated && !HasGovernedTranslation(
                    row,
                    document,
                    row.EvidenceLanguageCode,
                    languageLineage,
                    assessedUtc))
            {
                return Result(
                    LegendConnectResearchEvidenceDisposition.ObservationOnly,
                    "research_translation_not_governed",
                    lineage);
            }

            if (!translated && !PassageDirectlySupportsClaim(
                    row.Statement,
                    row.SupportingExcerpt!))
            {
                return Result(
                    LegendConnectResearchEvidenceDisposition.Rejected,
                    "research_claim_passage_entailment_failed",
                    lineage);
            }
        }

        if (HasCircularCitation(source.SourceIdentity, sourceById))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_citation_cycle",
                lineage);
        }

        if (row.Support == LegendConnectResearchEvidenceSupport.CitationChain)
        {
            var terminal = FindDirectSupportingSource(
                row,
                sourceById,
                directRows,
                new HashSet<string>(StringComparer.Ordinal));
            if (terminal is null)
            {
                return Result(
                    LegendConnectResearchEvidenceDisposition.Rejected,
                    "research_citation_chain_without_direct_support",
                    lineage);
            }

            var terminalRow = directRows[(row.ClaimIdentity, terminal)]
                .First(item => string.Equals(
                    item.Statement.Trim(),
                    row.Statement.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            var terminalAssessment = Evaluate(
                terminalRow,
                duplicateEvidenceIds,
                classificationConflictEvidenceIds,
                sourceById,
                documentById,
                citationById,
                contentHashCounts,
                directRows,
                assessedUtc,
                languageLineage);
            if (!terminalAssessment.PreliminarilyAdmissible)
            {
                return Result(
                    LegendConnectResearchEvidenceDisposition.Rejected,
                    "research_citation_chain_without_admissible_direct_support",
                    terminalAssessment.Decision.IndependentLineageIdentity);
            }

            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_citation_chain_dependent_observation",
                terminalAssessment.Decision.IndependentLineageIdentity);
        }

        if (HasConflictingTimestamps(source))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_timestamps_conflict",
                lineage);
        }

        if (source.PublishedUtc is not { } publishedUtc)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_publication_timestamp_missing",
                lineage);
        }
        if (NormalizeUtc(publishedUtc) > NormalizeUtc(document.RetrievedUtc) ||
            NormalizeUtc(document.RetrievedUtc) > assessedUtc)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_claim_timestamps_invalid",
                lineage);
        }

        var contentTimestamp = new[]
            {
                publishedUtc,
                source.UpdatedUtc,
                source.EffectiveUtc
            }
            .Where(item => item.HasValue)
            .Select(item => NormalizeUtc(item.GetValueOrDefault()))
            .Max();
        if (standard.MaximumAge is { } maximumAge &&
            NormalizeUtc(row.AsOfUtc ?? assessedUtc) - contentTimestamp > maximumAge)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_stale_for_claim",
                lineage);
        }

        if (source.SourceClass == LegendConnectResearchSourceClass.AnonymousOrUnverifiableContent)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_anonymous_or_unverifiable",
                lineage);
        }

        if (!HasRequiredAuthorship(source))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_authorship_missing",
                lineage);
        }

        if (!source.ProvenanceComplete)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_provenance_incomplete",
                lineage);
        }

        if (RequiresMethodology(source.SourceClass) && !source.MethodologyAvailable)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_methodology_missing",
                lineage);
        }

        if ((source.LineageKind is
                LegendConnectResearchSourceLineageKind.Copied or
                LegendConnectResearchSourceLineageKind.Syndicated or
                LegendConnectResearchSourceLineageKind.PressReleaseDerived or
                LegendConnectResearchSourceLineageKind.CommonOrigin) &&
            string.IsNullOrWhiteSpace(source.OriginalSourceIdentity) &&
            string.IsNullOrWhiteSpace(source.CommonOriginIdentity))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_source_dependent_lineage_unresolved",
                lineage);
        }

        if (row.Support == LegendConnectResearchEvidenceSupport.Observation ||
            row.StatementKind is
                LegendConnectResearchStatementKind.Analysis or
                LegendConnectResearchStatementKind.Opinion or
                LegendConnectResearchStatementKind.Inference)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_nonfactual_statement_preserved_as_observation",
                lineage);
        }

        if (source.SourceClass == LegendConnectResearchSourceClass.UserGeneratedContent)
        {
            return row.StatementKind is
                    LegendConnectResearchStatementKind.FirsthandExperience or
                    LegendConnectResearchStatementKind.PublicSentiment or
                    LegendConnectResearchStatementKind.PublishedStatement
                ? Result(
                    LegendConnectResearchEvidenceDisposition.CorroboratingEvidence,
                    "research_user_generated_claim_specific_support",
                    lineage,
                    true)
                : Result(
                    LegendConnectResearchEvidenceDisposition.ObservationOnly,
                    "research_user_generated_unrelated_fact_observation",
                    lineage);
        }

        var requiredScope = row.RequiredAuthorityScope ==
            LegendConnectResearchAuthorityScope.GeneralRecord
                ? standard.DefaultAuthorityScope
                : row.RequiredAuthorityScope;
        var scopeMatches = source.AuthorityScopes?.Contains(requiredScope) == true;
        if (source.SourceClass == LegendConnectResearchSourceClass.FirstPartyCompanyStatement &&
            (!scopeMatches || !IsFirstPartyStatementWithinAuthority(row.StatementKind, requiredScope)))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_first_party_authority_outside_claim_scope",
                lineage);
        }

        if (!scopeMatches && RequiresClaimSpecificAuthorityScope(source.SourceClass))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_authority_outside_claim_scope",
                lineage);
        }

        if (!standard.AcceptedSourceClasses.Contains(source.SourceClass))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_class_observation_only",
                lineage);
        }

        if (standard.AllowDefinitiveControllingRecord &&
            source.IsControllingRecord &&
            scopeMatches &&
            standard.ControllingSourceClasses.Contains(source.SourceClass) &&
            row.Support == LegendConnectResearchEvidenceSupport.Direct)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ControllingEvidence,
                "research_definitive_controlling_record",
                lineage,
                true);
        }

        return Result(
            LegendConnectResearchEvidenceDisposition.CorroboratingEvidence,
            scopeMatches
                ? "research_direct_claim_specific_support"
                : "research_direct_independent_support",
            lineage,
            true);
    }

    private static bool HasCompleteLineage(
        EvidenceRow row,
        LegendConnectResearchSourceIdentity source,
        LegendConnectRetrievedDocument document,
        LegendConnectCitation citation) =>
        string.Equals(document.SourceIdentity, row.SourceIdentity, StringComparison.Ordinal) &&
        string.Equals(citation.SourceIdentity, row.SourceIdentity, StringComparison.Ordinal) &&
        string.Equals(citation.DocumentIdentity, row.DocumentIdentity, StringComparison.Ordinal) &&
        string.Equals(document.CanonicalUri, source.CanonicalUri, StringComparison.Ordinal) &&
        string.Equals(citation.CanonicalUri, source.CanonicalUri, StringComparison.Ordinal) &&
        NormalizeUtc(citation.RetrievedUtc) == NormalizeUtc(document.RetrievedUtc);

    private static LegendConnectResearchMaterialClaimEvidence Materialize(
        EvaluatedEvidence evaluated,
        LegendConnectResearchSourceIdentity source,
        LegendConnectRetrievedDocument document,
        LegendConnectCitation citation,
        DateTime publishedUtc,
        DateTime assessedUtc,
        LegendConnectResearchLanguageLineage? languageLineage,
        int requestedMinimum)
    {
        var row = evaluated.Row;
        var standard = StandardFor(row.Subject);
        var applicableScope = row.RequiredAuthorityScope ==
            LegendConnectResearchAuthorityScope.GeneralRecord
                ? standard.DefaultAuthorityScope
                : row.RequiredAuthorityScope;
        _ = TryLocateExactPassage(row.SupportingExcerpt, document, out var passage);
        var exactPassage = passage!;
        var normalizedClaimIdentity = NormalizeClaimIdentity(
            row.ClaimIdentity,
            row.Subject,
            applicableScope);
        var materialIdentity = LegendLanguageIdentity.TextHash(string.Join(
            '|',
            "research-material-claim|v1",
            normalizedClaimIdentity,
            source.SourceIdentity,
            exactPassage.LocationIdentity,
            row.EvidenceIdentity,
            row.IsContradiction));
        var translation = ResolveTranslationLineage(
            row,
            document,
            languageLineage,
            assessedUtc);
        var freshness = ResolveFreshness(
            source,
            standard,
            row.AsOfUtc ?? assessedUtc);
        var standardRank = evaluated.Decision.Disposition switch
        {
            LegendConnectResearchEvidenceDisposition.ControllingEvidence => 3,
            LegendConnectResearchEvidenceDisposition.CorroboratingEvidence => 2,
            _ => 1
        };
        var evidenceStandard = string.Join(
            '|',
            LegendConnectResearchContracts.ClaimEvidencePolicy,
            row.Subject,
            evaluated.Decision.Disposition,
            "minimum-independent:" + Math.Clamp(
                Math.Max(requestedMinimum, standard.MinimumIndependentSources),
                1,
                3));
        var initialState = freshness == LegendConnectResearchFreshnessState.Stale
            ? LegendConnectResearchClaimVerificationState.Stale
            : evaluated.Decision.Disposition ==
                LegendConnectResearchEvidenceDisposition.ControllingEvidence
                ? LegendConnectResearchClaimVerificationState.VerifiedByControllingEvidence
                : LegendConnectResearchClaimVerificationState.SourceReportedButNotIndependentlyVerified;
        var relationship = row.IsContradiction
            ? LegendConnectResearchEvidenceRelationship.Contradiction
            : row.Support == LegendConnectResearchEvidenceSupport.Direct
                ? LegendConnectResearchEvidenceRelationship.DirectSupport
                : LegendConnectResearchEvidenceRelationship.Contextual;
        var premiseClaims = (row.PremiseClaimIdentities ?? [])
            .Select(item => NormalizeClaimIdentity(item, row.Subject, applicableScope))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var discriminatingClaim = string.IsNullOrWhiteSpace(row.DiscriminatingClaimIdentity)
            ? null
            : NormalizeClaimIdentity(
                row.DiscriminatingClaimIdentity,
                row.Subject,
                applicableScope);
        return new LegendConnectResearchMaterialClaimEvidence(
            materialIdentity,
            normalizedClaimIdentity,
            row.Statement.Trim(),
            source.SourceIdentity,
            document.DocumentIdentity,
            citation.CitationIdentity,
            exactPassage,
            source.SourceClass,
            NormalizeUtc(publishedUtc),
            NormalizeUtc(document.RetrievedUtc),
            row.Subject,
            applicableScope,
            relationship,
            evaluated.Decision.IndependentLineageIdentity,
            freshness,
            evidenceStandard,
            standardRank,
            Math.Clamp(
                Math.Max(requestedMinimum, standard.MinimumIndependentSources),
                1,
                3),
            translation,
            translation.TranslationApplied && translation.GovernedTranslationValidated
                ? LegendConnectResearchExtractionMethod.GovernedTranslationValidated
                : LegendConnectResearchExtractionMethod.ModelAssistedProposalValidatedAgainstExactPassage,
            new LegendConnectResearchMaterialClaimProvenance(
                row.EvidenceIdentity,
                source.SourceIdentity,
                document.DocumentIdentity,
                citation.CitationIdentity,
                exactPassage.LocationIdentity,
                document.ContentHash,
                LegendConnectResearchContracts.ClaimEvidencePolicy,
                assessedUtc,
                true,
                true,
                true,
                !HasConflictingTimestamps(source),
                true,
                LegendLanguageIdentity.TextHash(row.Statement)),
            row.StatementKind,
            initialState,
            premiseClaims,
            discriminatingClaim,
            row.CorrectsSourceIdentity);
    }

    internal static string NormalizeClaimIdentity(
        string proposedIdentity,
        LegendConnectResearchClaimSubject subject,
        LegendConnectResearchAuthorityScope scope)
    {
        var normalized = NormalizeClaimKey(proposedIdentity);
        return LegendLanguageIdentity.TextHash(string.Join(
            '|',
            "research-normalized-claim|v1",
            subject,
            scope,
            normalized));
    }

    private static bool TryLocateExactPassage(
        string? supportingExcerpt,
        LegendConnectRetrievedDocument document,
        out LegendConnectResearchPassageLocation? passage)
    {
        passage = null;
        if (string.IsNullOrWhiteSpace(supportingExcerpt) ||
            LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(supportingExcerpt))
            return false;
        var exact = supportingExcerpt.Trim();
        var start = document.ContentExcerpt.IndexOf(
            exact,
            StringComparison.OrdinalIgnoreCase);
        if (start < 0 || exact.Length > 800)
            return false;
        var sourcePassage = document.ContentExcerpt.Substring(start, exact.Length);
        var passageHash = LegendLanguageIdentity.TextHash(sourcePassage);
        passage = new LegendConnectResearchPassageLocation(
            document.DocumentIdentity,
            start,
            exact.Length,
            sourcePassage,
            passageHash,
            LegendLanguageIdentity.TextHash(string.Join(
                '|',
                "research-passage-location|v1",
                document.DocumentIdentity,
                start,
                exact.Length,
                passageHash)));
        return true;
    }

    private static bool PassageDirectlySupportsClaim(
        string statement,
        string supportingPassage)
    {
        var normalizedStatement = NormalizeEvidenceText(statement).ToLowerInvariant();
        var normalizedPassage = NormalizeEvidenceText(supportingPassage).ToLowerInvariant();
        return normalizedStatement.Length > 0 &&
               normalizedPassage.Contains(normalizedStatement, StringComparison.Ordinal);
    }

    private static string NormalizeClaimKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var token = new System.Text.StringBuilder();
        var pendingSeparator = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && token.Length > 0)
                    token.Append(' ');
                token.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
                continue;
            }
            pendingSeparator = true;
        }
        return token.ToString();
    }

    private static bool HasGovernedTranslation(
        EvidenceRow row,
        LegendConnectRetrievedDocument document,
        string evidenceLanguageCode,
        LegendConnectResearchLanguageLineage? languageLineage,
        DateTime assessedUtc)
    {
        if (languageLineage is null)
            return false;
        return languageLineage.TranslationReceipts.Any(item =>
            IsGovernedTranslationReceipt(
                row,
                document,
                evidenceLanguageCode,
                item,
                assessedUtc));
    }

    private static bool IsGovernedTranslationReceipt(
        EvidenceRow row,
        LegendConnectRetrievedDocument document,
        string evidenceLanguageCode,
        LegendConnectResearchTranslationReceipt item,
        DateTime assessedUtc) =>
        string.Equals(item.InputIdentity, document.ContentHash, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(item.ReceiptIdentity) &&
        !string.IsNullOrWhiteSpace(item.Transport) &&
        !string.IsNullOrWhiteSpace(item.OutputIdentity) &&
        string.Equals(
            item.SourceLanguageCode,
            document.DocumentLanguageCode,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            item.TargetLanguageCode,
            evidenceLanguageCode,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            item.State,
            "GovernedTranslationValidated",
            StringComparison.Ordinal) &&
        item.ValidatedProposalIdentities?.Contains(
            row.EvidenceIdentity,
            StringComparer.Ordinal) == true &&
        NormalizeUtc(item.ObservedUtc) >= NormalizeUtc(document.RetrievedUtc) &&
        NormalizeUtc(item.ObservedUtc) <= assessedUtc;

    private static LegendConnectResearchClaimTranslationLineage ResolveTranslationLineage(
        EvidenceRow row,
        LegendConnectRetrievedDocument document,
        LegendConnectResearchLanguageLineage? languageLineage,
        DateTime assessedUtc)
    {
        var documentLanguage = document.DocumentLanguageCode ?? "und";
        var evidenceLanguage = string.IsNullOrWhiteSpace(row.EvidenceLanguageCode)
            ? "und"
            : row.EvidenceLanguageCode;
        var translated = !string.Equals(
            documentLanguage,
            evidenceLanguage,
            StringComparison.OrdinalIgnoreCase);
        var receipt = languageLineage?.TranslationReceipts.FirstOrDefault(item =>
            IsGovernedTranslationReceipt(
                row,
                document,
                evidenceLanguage,
                item,
                assessedUtc));
        return new LegendConnectResearchClaimTranslationLineage(
            documentLanguage,
            evidenceLanguage,
            languageLineage?.FinalResponseLanguageCode ?? evidenceLanguage,
            translated,
            !translated || string.Equals(
                receipt?.State,
                "GovernedTranslationValidated",
                StringComparison.Ordinal),
            receipt?.ReceiptIdentity,
            translated ? receipt?.State ?? "TranslationLineageMissing" : "NotRequired");
    }

    private static LegendConnectResearchFreshnessState ResolveFreshness(
        LegendConnectResearchSourceIdentity source,
        EvidenceStandard standard,
        DateTime asOfUtc)
    {
        if (HasConflictingTimestamps(source))
            return LegendConnectResearchFreshnessState.ConflictingTimestamps;
        var timestamps = new[] { source.PublishedUtc, source.UpdatedUtc, source.EffectiveUtc }
            .Where(item => item.HasValue)
            .Select(item => NormalizeUtc(item.GetValueOrDefault()))
            .ToArray();
        if (timestamps.Length == 0)
            return LegendConnectResearchFreshnessState.Undated;
        return standard.MaximumAge is { } maximumAge &&
               NormalizeUtc(asOfUtc) - timestamps.Max() > maximumAge
            ? LegendConnectResearchFreshnessState.Stale
            : LegendConnectResearchFreshnessState.Current;
    }

    private static string NormalizeEvidenceText(string value) =>
        string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool HasCircularCitation(
        string sourceIdentity,
        IReadOnlyDictionary<string, LegendConnectResearchSourceIdentity> sourceById)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string current)
        {
            if (!visiting.Add(current))
                return true;
            if (!sourceById.TryGetValue(current, out var source))
            {
                visiting.Remove(current);
                return false;
            }
            foreach (var target in source.CitationTargetSourceIdentities ?? [])
            {
                if (!visited.Contains(target) && Visit(target))
                    return true;
            }
            visiting.Remove(current);
            visited.Add(current);
            return false;
        }

        return Visit(sourceIdentity);
    }

    private static string? FindDirectSupportingSource(
        EvidenceRow row,
        IReadOnlyDictionary<string, LegendConnectResearchSourceIdentity> sourceById,
        IReadOnlyDictionary<(string ClaimIdentity, string SourceIdentity), EvidenceRow[]> directRows,
        HashSet<string> visited)
    {
        if (!visited.Add(row.SourceIdentity) ||
            !sourceById.TryGetValue(row.SourceIdentity, out var source))
            return null;

        foreach (var target in source.CitationTargetSourceIdentities ?? [])
        {
            if (directRows.TryGetValue((row.ClaimIdentity, target), out var targetRows) &&
                targetRows.Any(item => string.Equals(
                    item.Statement.Trim(),
                    row.Statement.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            {
                return target;
            }

            var chained = FindDirectSupportingSource(
                row with { SourceIdentity = target },
                sourceById,
                directRows,
                visited);
            if (chained is not null)
                return chained;
        }
        return null;
    }

    private static bool HasConflictingTimestamps(LegendConnectResearchSourceIdentity source)
    {
        var retrieved = NormalizeUtc(source.RetrievedUtc);
        var published = source.PublishedUtc is { } publishedUtc ? NormalizeUtc(publishedUtc) : (DateTime?)null;
        var updated = source.UpdatedUtc is { } updatedUtc ? NormalizeUtc(updatedUtc) : (DateTime?)null;
        return updated.HasValue && published.HasValue && updated < published ||
               published.HasValue && published > retrieved.AddMinutes(5) ||
               updated.HasValue && updated > retrieved.AddMinutes(5);
    }

    private static bool HasRequiredAuthorship(LegendConnectResearchSourceIdentity source) =>
        source.SourceClass switch
        {
            LegendConnectResearchSourceClass.PeerReviewedOriginalResearch or
            LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority or
            LegendConnectResearchSourceClass.IndependentProfessionalReporting or
            LegendConnectResearchSourceClass.IndependentSecondaryAnalysis or
            LegendConnectResearchSourceClass.OpinionOrCommentary or
            LegendConnectResearchSourceClass.UserGeneratedContent =>
                !string.IsNullOrWhiteSpace(source.Author),
            _ => !string.IsNullOrWhiteSpace(source.Author) ||
                 !string.IsNullOrWhiteSpace(source.Publisher)
        };

    private static bool RequiresMethodology(LegendConnectResearchSourceClass sourceClass) =>
        sourceClass is
            LegendConnectResearchSourceClass.PeerReviewedOriginalResearch or
            LegendConnectResearchSourceClass.SystematicReviewOrRecognizedScientificMedicalAuthority;

    private static bool IsFirstPartyStatementWithinAuthority(
        LegendConnectResearchStatementKind statementKind,
        LegendConnectResearchAuthorityScope scope) =>
        (statementKind is
            LegendConnectResearchStatementKind.SourceAssertion or
            LegendConnectResearchStatementKind.PublishedStatement or
            LegendConnectResearchStatementKind.Fact) &&
        (scope is
            LegendConnectResearchAuthorityScope.OwnPublishedPolicy or
            LegendConnectResearchAuthorityScope.OwnProductOrService or
            LegendConnectResearchAuthorityScope.OwnOperations or
            LegendConnectResearchAuthorityScope.CurrentEventRecord);

    private static bool RequiresClaimSpecificAuthorityScope(
        LegendConnectResearchSourceClass sourceClass) =>
        sourceClass is
            LegendConnectResearchSourceClass.PrimaryOfficialRecord or
            LegendConnectResearchSourceClass.LegislatureRegulatorCourtOrGovernmentAuthority or
            LegendConnectResearchSourceClass.RegulatoryFilingOrAuditedFinancialReport or
            LegendConnectResearchSourceClass.OfficialProductOrTechnicalDocumentation or
            LegendConnectResearchSourceClass.FirstPartyCompanyStatement;

    private static string ResolveIndependentLineage(
        LegendConnectResearchSourceIdentity source,
        LegendConnectRetrievedDocument document,
        IReadOnlyDictionary<string, int> contentHashCounts)
    {
        if (!string.IsNullOrWhiteSpace(source.CommonOriginIdentity))
            return "origin:" + source.CommonOriginIdentity;
        if (!string.IsNullOrWhiteSpace(source.OriginalSourceIdentity))
            return "origin:" + source.OriginalSourceIdentity;
        if (!string.IsNullOrWhiteSpace(document.ContentHash) &&
            contentHashCounts.TryGetValue(document.ContentHash, out var count) &&
            (count > 1 || source.LineageKind is
                LegendConnectResearchSourceLineageKind.Copied or
                LegendConnectResearchSourceLineageKind.Syndicated or
                LegendConnectResearchSourceLineageKind.PressReleaseDerived or
                LegendConnectResearchSourceLineageKind.CommonOrigin))
        {
            return "content:" + document.ContentHash;
        }
        return "source:" + source.SourceIdentity;
    }

    private static IReadOnlyDictionary<string, T> UniqueBy<T>(
        IEnumerable<T> values,
        Func<T, string> identity) =>
        values.Where(item => !string.IsNullOrWhiteSpace(identity(item)))
            .GroupBy(identity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static EvidenceStandard Standard(
        LegendConnectResearchClaimSubject subject,
        TimeSpan? maximumAge,
        int minimumIndependentSources,
        LegendConnectResearchAuthorityScope defaultAuthorityScope,
        bool allowDefinitiveControllingRecord,
        IReadOnlyList<LegendConnectResearchSourceClass> acceptedSourceClasses,
        IReadOnlyList<LegendConnectResearchSourceClass> controllingSourceClasses) =>
        new(
            subject,
            maximumAge,
            minimumIndependentSources,
            defaultAuthorityScope,
            allowDefinitiveControllingRecord,
            acceptedSourceClasses,
            controllingSourceClasses);

    internal sealed record EvidenceStandard(
        LegendConnectResearchClaimSubject Subject,
        TimeSpan? MaximumAge,
        int MinimumIndependentSources,
        LegendConnectResearchAuthorityScope DefaultAuthorityScope,
        bool AllowDefinitiveControllingRecord,
        IReadOnlyList<LegendConnectResearchSourceClass> AcceptedSourceClasses,
        IReadOnlyList<LegendConnectResearchSourceClass> ControllingSourceClasses);

    private sealed record EvidenceRow(
        string EvidenceIdentity,
        string ClaimIdentity,
        string Statement,
        string SourceIdentity,
        string DocumentIdentity,
        string CitationIdentity,
        LegendConnectResearchClaimSubject Subject,
        LegendConnectResearchStatementKind StatementKind,
        LegendConnectResearchEvidenceSupport Support,
        LegendConnectResearchAuthorityScope RequiredAuthorityScope,
        DateTime? AsOfUtc,
        string? SupportingExcerpt,
        string EvidenceLanguageCode,
        LegendConnectResearchExtractionMethod ExtractionMethod,
        IReadOnlyList<string>? PremiseClaimIdentities,
        string? DiscriminatingClaimIdentity,
        string? CorrectsSourceIdentity,
        bool IsContradiction)
    {
        internal static EvidenceRow From(LegendConnectClaimEvidence item) => new(
            item.EvidenceIdentity,
            item.ClaimIdentity,
            item.Statement,
            item.SourceIdentity,
            item.DocumentIdentity,
            item.CitationIdentity,
            item.Subject,
            item.StatementKind,
            item.Support,
            item.RequiredAuthorityScope,
            item.AsOfUtc,
            item.SupportingExcerpt,
            item.EvidenceLanguageCode,
            item.ExtractionMethod,
            item.PremiseClaimIdentities,
            item.DiscriminatingClaimIdentity,
            item.CorrectsSourceIdentity,
            false);

        internal static EvidenceRow From(LegendConnectContradictingEvidence item) => new(
            item.EvidenceIdentity,
            item.ClaimIdentity,
            item.Statement,
            item.SourceIdentity,
            item.DocumentIdentity,
            item.CitationIdentity,
            item.Subject,
            item.StatementKind,
            item.Support,
            item.RequiredAuthorityScope,
            item.AsOfUtc,
            item.SupportingExcerpt,
            item.EvidenceLanguageCode,
            item.ExtractionMethod,
            item.PremiseClaimIdentities,
            item.DiscriminatingClaimIdentity,
            item.CorrectsSourceIdentity,
            true);
    }

    private sealed record EvaluatedEvidence(
        EvidenceRow Row,
        LegendConnectResearchEvidenceAdmissibility Decision,
        bool PreliminarilyAdmissible);
}

internal sealed record LegendConnectResearchEvidencePolicyAssessment(
    IReadOnlyList<LegendConnectResearchMaterialClaimEvidence> MaterialEvidence,
    IReadOnlyList<LegendConnectResearchEvidenceAdmissibility> Admissibility,
    int RequestedMinimumIndependentSources);
