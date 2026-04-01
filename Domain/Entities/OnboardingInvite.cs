namespace Domain.Entities;

public class OnboardingInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TokenHash { get; set; } = "";

    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";
    public string Email     { get; set; } = "";
    public string? NormalizedEmail { get; set; }
    public string RoleType  { get; set; } = "";

    public string Status { get; set; } = "Pending"; // Pending, Submitted, Expired, Revoked

    public DateTime CreatedUtc  { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }

    public string CreatedBy { get; set; } = "";

    public ICollection<OnboardingSubmission> Submissions { get; set; } = new List<OnboardingSubmission>();
}
