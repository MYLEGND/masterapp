using Infrastructure.Leads;

namespace Infrastructure.Reception;

/// <summary>Context injection, never a claim that text was spoken or heard.</summary>
internal sealed record ReceptionistApplicationUpdate(string EventId, string? DelegationId, string Type, string Content);

internal sealed record ReceptionistApplicationServiceDefinition
{
    public string ServiceKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string DiscoveryQuestion { get; init; } = string.Empty;
    public string PublicSummary { get; init; } = string.Empty;
}

internal sealed record ReceptionistApplicationKnowledge
{
    public IReadOnlyList<ReceptionistApplicationAnswer> Answers { get; init; } = [];
}

internal sealed record ReceptionistApplicationAnswer
{
    public string Key { get; init; } = string.Empty;
    public string? ServiceKey { get; init; }
    public IReadOnlyList<string> Questions { get; init; } = [];
    public string Answer { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? AsOfDate { get; init; }
}

internal sealed record ReceptionistApplicationPriorAction(Guid ActionId, string State);

/// <summary>The implementation invokes the existing admission and canonical inquiry authorities.</summary>
internal interface IReceptionistApplicationActions
{
    Task<IReadOnlyList<ReceptionistApplicationPriorAction>> PriorAsync(ReceptionistCallContext call, CancellationToken ct);
    Task<ReceptionistActionClaim> ClaimAsync(ReceptionistCallContext call, string logicalKey, string factsHash,
        string confirmationReceipt, Guid leaseToken, CancellationToken ct);
    Task<ReceptionistInquiryPersistence> PersistAsync(ReceptionistCallContext call, Guid actionId,
        ConfirmedReceptionistInquiry facts, string confirmationReceipt, CancellationToken ct);
    Task<ReceptionistInquiryReadback?> ReadBackAsync(ReceptionistCallContext call, Guid actionId, CancellationToken ct);
    Task OutcomeAsync(Guid actionId, string state, string? actualReference, string receipt, CancellationToken ct);
}
