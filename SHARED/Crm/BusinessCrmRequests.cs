using System.ComponentModel.DataAnnotations;

namespace Shared.Crm;

public sealed class BusinessCrmQuickViewRequest
{
    [Required, MaxLength(450)] public string ClientUserId { get; set; } = "";
    [Required, MaxLength(200)] public string Revision { get; set; } = "";
    [MaxLength(200)] public string? FirstName { get; set; }
    [MaxLength(200)] public string? LastName { get; set; }
    [EmailAddress, MaxLength(320)] public string? Email { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(50)] public string? Phone2 { get; set; }
    public DateTime? Dob { get; set; }
    [MaxLength(50)] public string? Gender { get; set; }
    [MaxLength(500)] public string? AddressLine { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(120)] public string? State { get; set; }
    [MaxLength(120)] public string? County { get; set; }
    [MaxLength(30)] public string? ZipCode { get; set; }
    [MaxLength(30)] public string? Age { get; set; }
    [MaxLength(100)] public string? Btc { get; set; }
    [MaxLength(20)] public string? CrmStatus { get; set; }
    [MaxLength(20)] public string? CrmPriority { get; set; }
    [MaxLength(40)] public string? ContactStatus { get; set; }
    public DateTime? CrmNextDate { get; set; }
    [MaxLength(2000)] public string? CrmNextText { get; set; }
    [MaxLength(2000)] public string? CrmTags { get; set; }
    [MaxLength(20000)] public string? AgentNotes { get; set; }
    [MaxLength(60)] public string? PipelineStage { get; set; }
    [MaxLength(500)] public string? MeetingLocation { get; set; }
    [MaxLength(2000)] public string? ZoomJoinUrl { get; set; }
    public bool? UsePersonalZoomLink { get; set; }
    [MaxLength(100)] public string? MeetingTime { get; set; }
    [Range(5, 1440)] public int? MeetingDurationMinutes { get; set; }
    [MaxLength(40)] public string? WaitingOn { get; set; }
    [MaxLength(4000)] public string? PinnedBrief { get; set; }
    public bool DocIdReceived { get; set; }
    public bool DocAppSent { get; set; }
    public bool DocAppSigned { get; set; }
    public bool DocPolicyDelivered { get; set; }
    public bool DocReviewBooked { get; set; }
    [MaxLength(2000)] public string? Watchers { get; set; }
    [MaxLength(4000)] public string? MentionNote { get; set; }
}

public sealed class BusinessCrmActivityRequest
{
    [Required, MaxLength(450)] public string ClientUserId { get; set; } = "";
    [Required, MaxLength(200)] public string Revision { get; set; } = "";
    [Required, MaxLength(40)] public string Type { get; set; } = "Note";
    public DateTime? Date { get; set; }
    [Required, MaxLength(20000)] public string Note { get; set; } = "";
    [MaxLength(500)] public string? Location { get; set; }
    [MaxLength(2000)] public string? MeetingLink { get; set; }
}

public sealed class BusinessCrmReorderRequest
{
    [Required, MaxLength(60)] public string Bucket { get; set; } = "";
    [MinLength(1), MaxLength(1000)] public List<string> Ids { get; set; } = [];
    public Dictionary<string, string> Revisions { get; set; } = [];
}

public sealed class BusinessCrmBulkRequest
{
    [MinLength(1), MaxLength(1000)] public List<string> ClientUserIds { get; set; } = [];
    public Dictionary<string, string> Revisions { get; set; } = [];
    [MaxLength(60)] public string? PipelineStage { get; set; }
    public DateTime? CrmNextDate { get; set; }
    [MaxLength(2000)] public string? CrmNextText { get; set; }
    [MaxLength(20)] public string? CrmPriority { get; set; }
    [MaxLength(2000)] public string? CrmTags { get; set; }
    [MaxLength(20000)] public string? SharedNote { get; set; }
    [MaxLength(40)] public string? WaitingOn { get; set; }
}
