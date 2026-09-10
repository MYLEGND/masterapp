using Domain.Messaging;

namespace Infrastructure.Messaging;

/// <summary>
/// The one authority that types a request as an inspection of records this
/// deployment owns, and therefore as a request that must be served from an
/// authenticated governed read receipt rather than from provider recollection
/// or the public internet.
///
/// It carries no vocabulary: no ownership phrase list, no demand phrase list,
/// no subject nouns and no substring routing. The decision is made only from
/// the admitted relations of the governed meaning graph that the curriculum
/// authority already produced for the request, so there is exactly one
/// analysis and one classification. When that analysis admitted no relation of
/// the required kind the classification fails closed and names the exact
/// missing governed artifact.
/// </summary>
public static class LegendConnectOwnedRecordRequest
{
    /// <summary>
    /// The governed relation kind that must be admitted for an owned-record
    /// inspection intent to be established. It is a
    /// <c>LegendLanguageMeaningRelation.RelationKind</c> value; absent such a
    /// relation no authority in this system can type the request.
    /// </summary>
    public const string RequiredRelationKind = "owned_record_state_inspection";

    /// <summary>
    /// The explicit outcome when the governed meaning-graph analysis could not
    /// run. It is never collapsed into "no intent": the caller must fail closed
    /// onto mandatory governed inspection and surface the diagnostic.
    /// </summary>
    public static LegendConnectOwnedRecordClassification AnalysisUnavailable(
        string diagnostic) =>
        new(
            LegendConnectOwnedRecordIntent.AnalysisUnavailable,
            RequiresGovernedReadReceipt: false,
            MissingRelationKind: RequiredRelationKind,
            Diagnostic: diagnostic);

    /// <summary>
    /// Types the request from the admitted relations of its governed meaning
    /// graph. A null graph means the analysis never completed, which is
    /// reported as the missing required relation rather than guessed.
    /// </summary>
    public static LegendConnectOwnedRecordClassification Classify(
        LegendConnectUtteranceMeaningGraphSnapshot? graph)
    {
        if (graph is null)
        {
            return new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.Unknown,
                RequiresGovernedReadReceipt: false,
                MissingRelationKind: RequiredRelationKind);
        }

        var admitted = graph.Relations
            .Where(relation => string.Equals(
                relation.RelationKind,
                RequiredRelationKind,
                StringComparison.Ordinal))
            .ToList();

        if (admitted.Count == 0)
        {
            return new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.Unknown,
                RequiresGovernedReadReceipt: false,
                MissingRelationKind: RequiredRelationKind);
        }

        // Contradiction fails closed. When the same anchor pair also carries a
        // competing relation kind, the governed evidence does not settle what
        // the request means, so the intent is not established and the competing
        // kinds are reported instead of silently resolved.
        var contradicting = admitted
            .SelectMany(required => graph.Relations.Where(other =>
                other.SourceNodeIndex == required.SourceNodeIndex &&
                other.TargetNodeIndex == required.TargetNodeIndex &&
                !string.Equals(
                    other.RelationKind,
                    RequiredRelationKind,
                    StringComparison.Ordinal)))
            .Select(other => other.RelationKind)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();

        if (contradicting.Count > 0)
        {
            return new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.Unknown,
                RequiresGovernedReadReceipt: false,
                MissingRelationKind: RequiredRelationKind,
                Diagnostic:
                    "owned_record_relation_contradicted: " +
                    string.Join(",", contradicting));
        }

        return new LegendConnectOwnedRecordClassification(
            LegendConnectOwnedRecordIntent.OwnedRecordStateInspection,
            RequiresGovernedReadReceipt: true,
            MissingRelationKind: null);
    }
}
