namespace AgentPortal.Models;

public sealed record BusinessAccountReconciliationChoice(Guid BusinessId, string Name, string Key, string? Domain, bool Selected);
public sealed record BusinessAccountReconciliationViewModel(Guid ProfileId, string Email,
    IReadOnlyList<BusinessAccountReconciliationChoice> Choices, string ExpectedAssociations);
