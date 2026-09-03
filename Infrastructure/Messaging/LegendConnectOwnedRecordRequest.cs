namespace Infrastructure.Messaging;

/// <summary>
/// The one authority that decides whether a request asks for the state of
/// records this deployment owns and serves from authenticated governed
/// resources.
///
/// The decision is deictic and grammatical, not a domain phrase map: it needs
/// first-person ownership reference (a possessive determiner, a first-person
/// possession predicate, or an explicit "in the/our &lt;store&gt;" locative)
/// together with a demand for present record state. No subject vocabulary
/// ("client", "lead", "renewal", ...) participates, so the rule generalizes
/// across domains and paraphrase instead of matching a fixed noun list.
///
/// Both the Founder chat inspection requirement and the research-need decision
/// use this single predicate. It is deliberately conservative in one direction
/// only: a request it accepts must be answered from an authenticated governed
/// read, never from provider recollection or the public internet.
/// </summary>
public static class LegendConnectOwnedRecordRequest
{
    private static readonly string[] OwnershipDeixis =
    [
        "our ", "ours", "my ", "mine",
        "we have", "we own", "we hold", "we keep", "we track",
        "do we have", "do we own", "i have", "i own",
        "in the portal", "in the system", "in the database",
        "in the crm", "in our", "on our", "from our", "of ours"
    ];

    private static readonly string[] RecordStateDemand =
    [
        "how many", "how much", "what is", "what are", "what was",
        "what were", "which", "list", "show", "count", "total",
        "status", "current", "currently", "latest", "today",
        "right now", "as of", "report", "inspect", "look up", "pull up"
    ];

    /// <summary>
    /// True when the request asks for the present state of records the
    /// deployment owns, and therefore requires an authenticated governed read
    /// receipt before it can be answered.
    /// </summary>
    public static bool RequestsOwnedRecordState(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.ToLowerInvariant();

        return OwnershipDeixis.Any(marker =>
                   normalized.Contains(marker, StringComparison.Ordinal)) &&
               RecordStateDemand.Any(demand =>
                   normalized.Contains(demand, StringComparison.Ordinal));
    }
}
