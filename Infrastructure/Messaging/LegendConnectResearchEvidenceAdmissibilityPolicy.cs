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
        DateTime assessedUtc)
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
        var directRowsByClaimAndSource = rows
            .Where(item => item.Support == LegendConnectResearchEvidenceSupport.Direct)
            .GroupBy(item => (item.ClaimIdentity, item.SourceIdentity))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var evaluated = rows.Select(row => Evaluate(
                row,
                duplicateEvidenceIds,
                sourceById,
                documentById,
                citationById,
                contentHashCounts,
                directRowsByClaimAndSource,
                assessedUtc))
            .ToArray();

        var resolved = ResolveClaimGroups(evaluated, boundedMinimum, assessedUtc);
        var admittedClaims = resolved
            .Where(item =>
                !item.Row.IsContradiction &&
                (item.Decision.Disposition is
                    LegendConnectResearchEvidenceDisposition.ControllingEvidence or
                    LegendConnectResearchEvidenceDisposition.CorroboratingEvidence) &&
                item.ClaimAdmitted)
            .Select(item => item.Row.Claim!)
            .OrderBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal)
            .ToArray();
        var admittedContradictions = resolved
            .Where(item =>
                item.Row.IsContradiction &&
                (item.Decision.Disposition is
                    LegendConnectResearchEvidenceDisposition.ControllingEvidence or
                    LegendConnectResearchEvidenceDisposition.CorroboratingEvidence) &&
                item.ClaimAdmitted)
            .Select(item => item.Row.Contradiction!)
            .OrderBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal)
            .ToArray();

        var positiveClaimIds = admittedClaims
            .Select(item => item.ClaimIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var explicitConflict = admittedContradictions.Any(item =>
            positiveClaimIds.Contains(item.ClaimIdentity));
        var implicitConflict = admittedClaims
            .GroupBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .Any(group => group
                .Select(item => item.Statement.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
        var decisions = resolved.Select(item => item.Decision).ToArray();
        var required = resolved
            .Where(item => !item.Row.IsContradiction)
            .Select(item => item.RequiredIndependentSources)
            .DefaultIfEmpty(boundedMinimum)
            .Max();
        var independentCount = resolved
            .Where(item =>
                !item.Row.IsContradiction &&
                item.ClaimAdmitted &&
                (item.Decision.Disposition is
                    LegendConnectResearchEvidenceDisposition.ControllingEvidence or
                    LegendConnectResearchEvidenceDisposition.CorroboratingEvidence))
            .Select(item => item.Decision.IndependentLineageIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (explicitConflict || implicitConflict)
        {
            return new(
                LegendResearchEvidenceAssessmentState.UnresolvedConflict,
                admittedClaims,
                admittedContradictions,
                independentCount,
                required,
                "research_evidence_conflict_unresolved",
                decisions);
        }

        if (admittedClaims.Length == 0)
        {
            var rejectionReasons = decisions
                .Where(item => item.Disposition == LegendConnectResearchEvidenceDisposition.Rejected)
                .Select(item => item.ReasonCode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var reason = decisions.Any(item =>
                item.Disposition != LegendConnectResearchEvidenceDisposition.Rejected)
                    ? "research_evidence_standard_unmet"
                    : rejectionReasons.Length == 1
                        ? rejectionReasons[0]
                        : "research_evidence_admissibility_failed";
            return new(
                LegendResearchEvidenceAssessmentState.InsufficientEvidence,
                [],
                admittedContradictions,
                independentCount,
                required,
                reason,
                decisions);
        }

        return new(
            LegendResearchEvidenceAssessmentState.Conclusion,
            admittedClaims,
            admittedContradictions,
            independentCount,
            required,
            "research_claims_governed_by_source_authority",
            decisions);
    }

    internal static EvidenceStandard StandardFor(LegendConnectResearchClaimSubject subject) =>
        Standards.TryGetValue(subject, out var standard)
            ? standard
            : Standards[LegendConnectResearchClaimSubject.General];

    private static EvaluatedEvidence Evaluate(
        EvidenceRow row,
        IReadOnlySet<string> duplicateEvidenceIds,
        IReadOnlyDictionary<string, LegendConnectResearchSourceIdentity> sourceById,
        IReadOnlyDictionary<string, LegendConnectRetrievedDocument> documentById,
        IReadOnlyDictionary<string, LegendConnectCitation> citationById,
        IReadOnlyDictionary<string, int> contentHashCounts,
        IReadOnlyDictionary<(string ClaimIdentity, string SourceIdentity), EvidenceRow[]> directRows,
        DateTime assessedUtc)
    {
        var standard = StandardFor(row.Subject);
        var required = standard.MinimumIndependentSources;
        var emptyLineage = "unresolved:" + (row.SourceIdentity ?? "missing");

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
                    row.SourceIdentity,
                    row.Subject,
                    sourceById.TryGetValue(row.SourceIdentity, out var typedSource)
                        ? typedSource.SourceClass
                        : LegendConnectResearchSourceClass.UnknownSource,
                    disposition,
                    reason,
                    lineage,
                    row.IsContradiction,
                    assessedUtc,
                    PolicyIdentity),
                preliminarilyAdmissible,
                false,
                required);

        if (string.IsNullOrWhiteSpace(row.EvidenceIdentity) ||
            string.IsNullOrWhiteSpace(row.ClaimIdentity) ||
            string.IsNullOrWhiteSpace(row.Statement) ||
            duplicateEvidenceIds.Contains(row.EvidenceIdentity) ||
            !sourceById.TryGetValue(row.SourceIdentity, out var source) ||
            !documentById.TryGetValue(row.DocumentIdentity, out var document) ||
            !citationById.TryGetValue(row.CitationIdentity, out var citation) ||
            !HasCompleteLineage(row, source, document, citation))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_claim_lineage_incomplete",
                emptyLineage);
        }

        var lineage = ResolveIndependentLineage(source, document, contentHashCounts);
        if (row.Support == LegendConnectResearchEvidenceSupport.Direct &&
            !HasExactSupportingExcerpt(row.SupportingExcerpt, document.ContentExcerpt))
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
                "research_claim_direct_support_missing",
                lineage);
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
                sourceById,
                documentById,
                citationById,
                contentHashCounts,
                directRows,
                assessedUtc);
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

        var contentTimestamp = new[]
            {
                source.PublishedUtc,
                source.UpdatedUtc,
                source.EffectiveUtc
            }
            .Where(item => item.HasValue)
            .Select(item => (DateTime?)NormalizeUtc(item.GetValueOrDefault()))
            .DefaultIfEmpty()
            .Max();
        if (contentTimestamp is null)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.ObservationOnly,
                "research_source_date_missing",
                lineage);
        }
        if (standard.MaximumAge is { } maximumAge &&
            NormalizeUtc(row.AsOfUtc ?? assessedUtc) - NormalizeUtc(contentTimestamp.Value) > maximumAge)
        {
            return Result(
                LegendConnectResearchEvidenceDisposition.Rejected,
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

    private static EvaluatedEvidence[] ResolveClaimGroups(
        IReadOnlyList<EvaluatedEvidence> evaluated,
        int requestedMinimum,
        DateTime assessedUtc)
    {
        var resolved = evaluated.ToArray();
        foreach (var group in resolved.GroupBy(item =>
                     (item.Row.ClaimIdentity, item.Row.IsContradiction)))
        {
            if (group.Select(item => item.Row.Subject).Distinct().Count() != 1 ||
                group.Select(item => item.Row.StatementKind).Distinct().Count() != 1 ||
                group.Select(item => item.Row.RequiredAuthorityScope).Distinct().Count() != 1)
            {
                foreach (var item in group)
                {
                    var conflictingIndex = Array.IndexOf(resolved, item);
                    if (conflictingIndex < 0)
                        continue;
                    resolved[conflictingIndex] = item with
                    {
                        Decision = item.Decision with
                        {
                            Disposition = LegendConnectResearchEvidenceDisposition.Rejected,
                            ReasonCode = "research_claim_classification_conflict",
                            AssessedUtc = assessedUtc
                        },
                        PreliminarilyAdmissible = false,
                        ClaimAdmitted = false,
                        RequiredIndependentSources = requestedMinimum
                    };
                }
                continue;
            }

            var standard = StandardFor(group.First().Row.Subject);
            var required = Math.Clamp(
                Math.Max(requestedMinimum, standard.MinimumIndependentSources),
                1,
                3);
            var controlling = group
                .Where(item => item.PreliminarilyAdmissible &&
                               item.Decision.Disposition ==
                                   LegendConnectResearchEvidenceDisposition.ControllingEvidence)
                .ToArray();
            var admissible = group
                .Where(item => item.PreliminarilyAdmissible)
                .ToArray();
            var admitted = group.Key.IsContradiction
                ? admissible.Length > 0
                : controlling.Length > 0 ||
                  admissible.Select(item => item.Decision.IndependentLineageIdentity)
                      .Distinct(StringComparer.Ordinal)
                      .Count() >= required;

            foreach (var item in group)
            {
                var index = Array.IndexOf(resolved, item);
                if (index < 0)
                    continue;
                if (controlling.Length > 0 &&
                    item.PreliminarilyAdmissible &&
                    item.Decision.Disposition !=
                        LegendConnectResearchEvidenceDisposition.ControllingEvidence)
                {
                    resolved[index] = item with
                    {
                        Decision = item.Decision with
                        {
                            Disposition = LegendConnectResearchEvidenceDisposition.ObservationOnly,
                            ReasonCode = "research_primary_record_preferred_over_secondary_support",
                            AssessedUtc = assessedUtc
                        },
                        ClaimAdmitted = false,
                        RequiredIndependentSources = required
                    };
                }
                else
                {
                    resolved[index] = item with
                    {
                        ClaimAdmitted = admitted && item.PreliminarilyAdmissible,
                        RequiredIndependentSources = required
                    };
                }
            }
        }
        return resolved;
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
        string.Equals(citation.CanonicalUri, source.CanonicalUri, StringComparison.Ordinal);

    private static bool HasExactSupportingExcerpt(
        string? supportingExcerpt,
        string documentContent)
    {
        if (string.IsNullOrWhiteSpace(supportingExcerpt) ||
            LegendConnectResearchExternalDataPolicy.IsPotentialInstruction(supportingExcerpt))
            return false;
        var normalizedExcerpt = NormalizeEvidenceText(supportingExcerpt);
        var normalizedDocument = NormalizeEvidenceText(documentContent);
        return normalizedExcerpt.Length > 0 &&
               normalizedDocument.Contains(
                   normalizedExcerpt,
                   StringComparison.OrdinalIgnoreCase);
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
        bool IsContradiction,
        LegendConnectClaimEvidence? Claim,
        LegendConnectContradictingEvidence? Contradiction)
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
            false,
            item,
            null);

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
            true,
            null,
            item);
    }

    private sealed record EvaluatedEvidence(
        EvidenceRow Row,
        LegendConnectResearchEvidenceAdmissibility Decision,
        bool PreliminarilyAdmissible,
        bool ClaimAdmitted,
        int RequiredIndependentSources);
}

internal sealed record LegendConnectResearchEvidencePolicyAssessment(
    LegendResearchEvidenceAssessmentState State,
    IReadOnlyList<LegendConnectClaimEvidence> Claims,
    IReadOnlyList<LegendConnectContradictingEvidence> Contradictions,
    int IndependentSourceCount,
    int RequiredIndependentSourceCount,
    string ReasonCode,
    IReadOnlyList<LegendConnectResearchEvidenceAdmissibility> Admissibility);
