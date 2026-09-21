namespace Domain.Entities;

public sealed class WebsiteDomainBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string ProviderHostnameId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string CertificateStatus { get; set; } = "pending";
    public string VerificationJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
