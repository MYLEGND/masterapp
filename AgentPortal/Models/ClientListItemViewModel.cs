namespace AgentPortal.Models;

public sealed class ClientListItemViewModel
{
    public Guid Id { get; set; }
    public string ClientUserId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Phone2 { get; set; }
    public string? Age { get; set; }
    public string? Btc { get; set; }
    public string RecordType { get; set; } = "Lead";
    public string CrmStatus { get; set; } = "Lead";
    public string CrmPriority { get; set; } = "Normal";
    public DateTime? CrmLastTouch { get; set; }
    public DateTime? CrmNextDate { get; set; }
    public string? CrmNextText { get; set; }
    public string? CrmTags { get; set; }
    public string? AgentNotes { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? County { get; set; }
    public string? Gender { get; set; }
    public DateTime? DOB { get; set; }
    public string? MortgageLender { get; set; }
    public string? LoanAmount { get; set; }
    public string? OriginalLeadType { get; set; }
    public string? ContactStatus { get; set; }
    public string PipelineStage { get; set; } = ClientCrmMeta.DefaultPipelineStage;
    public double PipelineOrder { get; set; }
    public string? MeetingLocation { get; set; }
    public string? ZoomJoinUrl { get; set; }
    public bool UsePersonalZoomLink { get; set; }
    public string? MeetingTime { get; set; }
    public int MeetingDurationMinutes { get; set; } = 30;
    public string WaitingOn { get; set; } = ClientCrmMeta.DefaultWaitingOn;
    public string? PinnedBrief { get; set; }
    public DateTime StageEnteredUtc { get; set; } = DateTime.UtcNow;
    public decimal PaidAmount { get; set; }
    public decimal PersonalAmount { get; set; }
    public int StageAgeDays { get; set; }
    public int AttemptsToday { get; set; }
    public int AttemptsThisWeek { get; set; }
    public int AttemptsThisMonth { get; set; }
    public int AttemptsYear { get; set; }
    public int AttemptsLifetime { get; set; }
    public string? LastContactChannel { get; set; }
    public int DocChecklistCompletedCount { get; set; }
    public bool HasDuplicateEmail { get; set; }
    public bool HasDuplicatePhone { get; set; }
    public bool HasDuplicateHousehold { get; set; }
    public string? AssignedOwner { get; set; }
    public string? WatchersCsv { get; set; }
    public DateTime? LatestSubmissionUtc { get; set; }
    public int IntakeHistoryCount { get; set; }
    public string? LeadOriginLabel { get; set; }
    public string? LeadOriginTone { get; set; }
    public string? ProductInterestLabel { get; set; }
    public string? QuoteTypeLabel { get; set; }
    public string? AttributionSource { get; set; }
    public string? AttributionMedium { get; set; }
    public string? AttributionCampaign { get; set; }
    public string? LatestRecommendationSummary { get; set; }
    public string? IntakePageVariant { get; set; }
    public string? IntakePageMode { get; set; }
    public string? ProductionStatus { get; set; }
    public decimal ProductionAmount { get; set; }
    public decimal ProductionSubmittedAmount { get; set; }
    public decimal ProductionIssuedAmount { get; set; }
    public decimal ProductionPaidAmount { get; set; }
}
