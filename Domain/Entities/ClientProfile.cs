using System.ComponentModel.DataAnnotations.Schema;

namespace Domain.Entities;

public class ClientProfile
{
    // ============================
    // Primary Key
    // ============================
    public Guid Id { get; set; } = Guid.NewGuid();

    // Stable key across AgentPortal + ClientApp (Entra ObjectId or normalized UPN)
    public string ClientUserId { get; set; } = "";
    public string? ExternalIdentityObjectId { get; set; }

    // ============================
    // Core Client Identity
    // ============================
    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";
    public string Email     { get; set; } = "";
    public string? NormalizedEmail { get; set; }
    public string Phone     { get; set; } = "";

    // The client-owned profile photo is shared by both portals. It deliberately
    // belongs to this profile rather than to an Identity user or an app-local
    // file path so a client and an agent identity can never resolve each other.
    public byte[]? ProfileImageContent { get; set; }
    public string? ProfileImageContentType { get; set; }

    public DateTime? DOB { get; set; }
    public string MaritalStatus { get; set; } = "";

    // ============================
    // Significant Other (Household)
    // ============================
    public string? SignificantOtherFirstName { get; set; }
    public string? SignificantOtherLastName  { get; set; }
    public DateTime? SignificantOtherDOB     { get; set; }
    public string? SignificantOtherEmail     { get; set; }
    public string? SignificantOtherPhone     { get; set; }

    // ============================
    // Internal Agent Notes
    // ============================
    public string AgentNotes { get; set; } = "";

    // ============================
    // CRM (Persisted)
    // ============================
    // Keep values aligned with your UI dropdowns:
    // Status: Lead | Prospect | Active | Dormant
    // Priority: Low | Normal | High | Urgent
    public string? CrmStatus { get; set; } = "Active";
    public string? CrmPriority { get; set; } = "Normal";
    public DateTime? CrmLastTouch { get; set; }
    public DateTime? CrmNextDate { get; set; }
    public string? CrmNextText { get; set; }
    public string? CrmTags { get; set; }     // comma-separated
    public string? CrmNotes { get; set; }

    // Legacy/compatibility: some callers still expect a RecordType directly on ClientProfile.
    // The canonical value is stored inside serialized CrmNotes metadata, so this property is
    // *not* mapped to the database to avoid runtime "Invalid column name 'RecordType'" errors.
    // Controllers pull the value from ClientCrmMetaSerializer instead.
    [NotMapped]
    public string? RecordType { get; set; }

    // ============================
    // Auditing
    // ============================
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
