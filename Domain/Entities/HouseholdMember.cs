namespace Domain.Entities;

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
