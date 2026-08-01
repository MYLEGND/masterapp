namespace Domain.Entities;

/// <summary>
/// Legacy imported household-contact detail. This record is retained only for
/// explicit reconciliation and historic financial-form display; it must not be
/// used for account membership, entitlement, identity, or invitation decisions.
/// New household access is represented exclusively by HouseholdAccount,
/// HouseholdMembership, and HouseholdMemberInvitation.
/// </summary>
public class HouseholdMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Links to ClientProfile by ClientUserId (NOT Id) so it stays stable across apps
    public string ClientUserId { get; set; } = "";

    public string RelationshipType { get; set; } = "SignificantOther";

    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTime? DOB { get; set; }

    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
