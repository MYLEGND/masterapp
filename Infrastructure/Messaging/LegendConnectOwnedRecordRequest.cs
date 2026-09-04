namespace Infrastructure.Messaging;

/// <summary>
/// The typed intent a request carries about records this deployment owns.
/// </summary>
public enum LegendConnectOwnedRecordIntent
{
    /// <summary>
    /// No governed semantic transition established an owned-record meaning for
    /// this request. This is the fail-closed value: it is returned both when the
    /// request genuinely is not about owned records and when the governed
    /// evidence required to decide is absent.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A governed, production-eligible semantic relation established that the
    /// request demands the present state of records this deployment owns.
    /// </summary>
    OwnedRecordStateInspection = 1
}

/// <summary>
/// The typed outcome of classifying a request against the governed meaning
/// graph, including the receipt obligation and, when the classification could
/// not be made, the exact governed artifact that was missing.
/// </summary>
/// <param name="Intent">The established intent, or <see cref="LegendConnectOwnedRecordIntent.Unknown"/>.</param>
/// <param name="RequiresGovernedReadReceipt">
/// True only when <paramref name="Intent"/> is
/// <see cref="LegendConnectOwnedRecordIntent.OwnedRecordStateInspection"/>: the
/// answer may then not complete until a registered governed read tool returns a
/// receipt.
/// </param>
/// <param name="MissingSemanticTransition">
/// When the classification failed closed, the exact relation kind that must
/// exist as a production-eligible governed semantic relation before this
/// request can be typed. Null when the classification succeeded.
/// </param>
public readonly record struct LegendConnectOwnedRecordClassification(
    LegendConnectOwnedRecordIntent Intent,
    bool RequiresGovernedReadReceipt,
    string? MissingSemanticTransition);

/// <summary>
/// The one authority that types a request as an inspection of records this
/// deployment owns, and therefore as a request that must be served from an
/// authenticated governed read receipt rather than from provider recollection
/// or the public internet.
///
/// It carries no vocabulary: no ownership phrase list, no demand phrase list,
/// no subject nouns, and no substring routing. The decision is made only from
/// the governed semantic relations the existing meaning-graph analysis surfaced
/// for the request. When that analysis produced no production-eligible relation
/// of the required kind, this authority fails closed and names the exact
/// missing artifact instead of guessing from surface text.
/// </summary>
public static class LegendConnectOwnedRecordRequest
{
    /// <summary>
    /// The governed relation kind that must be promoted and production-eligible
    /// for an owned-record inspection intent to be established. It is a
    /// <c>LegendLanguageMeaningRelation.RelationKind</c> value; absent such a
    /// relation there is no authority in this system that can type the request.
    /// </summary>
    public const string RequiredRelationKind = "owned_record_state_inspection";

    /// <summary>
    /// Types the request from the production-eligible governed relation kinds
    /// the meaning-graph analysis surfaced for it.
    /// </summary>
    /// <param name="governedRelationKinds">
    /// The relation kinds of the production-eligible governed semantic
    /// relations selected for this request, or null when the meaning-graph
    /// analysis could not complete.
    /// </param>
    public static LegendConnectOwnedRecordClassification Classify(
        IReadOnlyCollection<string>? governedRelationKinds)
    {
        if (governedRelationKinds is null || governedRelationKinds.Count == 0)
        {
            return new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.Unknown,
                RequiresGovernedReadReceipt: false,
                MissingSemanticTransition: RequiredRelationKind);
        }

        var established = governedRelationKinds.Any(kind =>
            string.Equals(kind, RequiredRelationKind, StringComparison.Ordinal));

        return established
            ? new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.OwnedRecordStateInspection,
                RequiresGovernedReadReceipt: true,
                MissingSemanticTransition: null)
            : new LegendConnectOwnedRecordClassification(
                LegendConnectOwnedRecordIntent.Unknown,
                RequiresGovernedReadReceipt: false,
                MissingSemanticTransition: null);
    }
}
