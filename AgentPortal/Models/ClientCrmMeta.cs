using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPortal.Models;

public sealed class ClientCrmMeta
{
    public const string DefaultPipelineStage = "NewLead";
    public const string DefaultWaitingOn = "WaitingOnAgent";

    // Contact & profile enrichment (stored in CRM meta to avoid schema churn)
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? County { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone2 { get; set; }
    public string? Age { get; set; }
    public string? Btc { get; set; }
    public string? MortgageLender { get; set; }
    public string? LoanAmount { get; set; }
    public string? Gender { get; set; }
    public DateTime? DOB { get; set; }
    public string? CrmPriority { get; set; }
    public string? ContactStatus { get; set; }
    public DateTime? CrmNextDate { get; set; }
    public string? CrmNextText { get; set; }
    public string? CrmTags { get; set; }
    public string? AgentNotes { get; set; }

    // Aggregate counters (helpful when migrating to/from WorkstationLeadProfiles)
    public int AttemptsLifetime { get; set; }

    public string? RecordType { get; set; }
    public string PipelineStage { get; set; } = DefaultPipelineStage;
    public double PipelineOrder { get; set; }
    public DateTime StageEnteredUtc { get; set; } = DateTime.UtcNow;
    public string WaitingOn { get; set; } = DefaultWaitingOn;
    public string? PinnedBrief { get; set; }
    public string? MeetingLocation { get; set; }
    public string? ZoomJoinUrl { get; set; }
    public bool UsePersonalZoomLink { get; set; }
    public string? MeetingTime { get; set; }
    public int MeetingDurationMinutes { get; set; } = 30;
    public string? LastCalendarEventId { get; set; }
    public string? LastCalendarEventWebLink { get; set; }
    public string? LastContactChannel { get; set; }
    public ClientCrmDocChecklist DocChecklist { get; set; } = new();
    public ClientCrmOpportunityPlanningChecklist OpportunityPlanning { get; set; } = new();
    public ClientCrmCollaboration Collaboration { get; set; } = new();
    public List<ClientCrmActivity> Activities { get; set; } = new();
}

public sealed class ClientCrmDocChecklist
{
    public bool IdReceived { get; set; }
    public bool AppSent { get; set; }
    public bool AppSigned { get; set; }
    public bool PolicyDelivered { get; set; }
    public bool ReviewBooked { get; set; }
}

public sealed class ClientCrmOpportunityPlanningChecklist
{
    public bool LifeInsurance { get; set; }
    public bool DisabilityIncome { get; set; }
    public bool LongTermCare { get; set; }
    public bool CriticalIllness { get; set; }
    public bool TerminalIllness { get; set; }
    public bool AnnuityRetirement { get; set; }
    public bool MortgageProtection { get; set; }
    public bool FinalExpense { get; set; }
    public bool Medicare { get; set; }
    public bool Health { get; set; }
    public bool DentalVision { get; set; }
    public bool HospitalIndemnity { get; set; }
    public bool PersonalAuto { get; set; }
    public bool HomeRenters { get; set; }
    public bool UmbrellaLiability { get; set; }
    public bool FloodEarthquake { get; set; }
    public bool CommercialAuto { get; set; }
    public bool GeneralLiability { get; set; }
    public bool BusinessOwnersPolicy { get; set; }
    public bool WorkersComp { get; set; }
    public bool KeyPersonBuySell { get; set; }
    public bool GroupBenefits { get; set; }
}

public sealed class ClientCrmCollaboration
{
    public string? Owner { get; set; }
    public List<string> Watchers { get; set; } = new();
    public List<ClientCrmMentionNote> MentionNotes { get; set; } = new();
}

public sealed class ClientCrmMentionNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Note { get; set; } = "";
    public string? MentionedUser { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ClientCrmActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Note";
    public string Date { get; set; } = "";
    public string Note { get; set; } = "";
    public string? Location { get; set; }
    public string? MeetingLink { get; set; }
    public string? CalendarEventId { get; set; }
    public string? CalendarWebLink { get; set; }
    public string? OutcomeCode { get; set; }
    public string? Channel { get; set; }
    public bool IsSystem { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public static class ClientCrmMetaSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly string[] AllowedPipelineStages =
    {
        "NewLead",
        "Opportunities",
        "Contacted",
        "Qualified",
        "Client",
        "BusinessClient",
        "MeetingScheduled",
        "ProposalSent",
        "ApplicationStarted",
        "Submitted",
        "ClosedLost",
        "Nurture"
    };

    public static readonly string[] AllowedRecordTypes =
    {
        "Lead",
        "Client",
        "BusinessClient"
    };

    public static readonly string[] AllowedWaitingOnStates =
    {
        "WaitingOnAgent",
        "WaitingOnClient",
        "WaitingOnCarrier",
        "WaitingOnUnderwriting",
        "WaitingOnDocs"
    };

    public static readonly string[] AllowedContactStatuses =
    {
        "NotSet",
        "NoContactYet",
        "AttemptingContact",
        "Connected",
        "Quoted",
        "WaitingOnDecision",
        "Unresponsive"
    };

    public static ClientCrmMeta Deserialize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ClientCrmMeta();

        try
        {
            var meta = JsonSerializer.Deserialize<ClientCrmMeta>(raw, JsonOptions) ?? new ClientCrmMeta();
            Normalize(meta);
            return meta;
        }
        catch
        {
            return new ClientCrmMeta();
        }
    }

    public static string Serialize(ClientCrmMeta meta)
    {
        Normalize(meta);
        return JsonSerializer.Serialize(meta, JsonOptions);
    }

    public static void Normalize(ClientCrmMeta meta)
    {
        var normalizedStage = NormalizePipelineStage(meta.PipelineStage);
        if (!string.Equals(meta.PipelineStage, normalizedStage, StringComparison.Ordinal))
            meta.StageEnteredUtc = DateTime.UtcNow;

        meta.PipelineStage = normalizedStage;
        meta.RecordType = string.IsNullOrWhiteSpace(meta.RecordType)
            ? null
            : NormalizeRecordType(meta.RecordType, defaultToLead: false);
        if (double.IsNaN(meta.PipelineOrder) || double.IsInfinity(meta.PipelineOrder))
            meta.PipelineOrder = 0;
        if (meta.StageEnteredUtc == default)
            meta.StageEnteredUtc = DateTime.UtcNow;

        meta.WaitingOn = NormalizeWaitingOn(meta.WaitingOn);
        meta.PinnedBrief = Clean(meta.PinnedBrief);
        meta.MeetingLocation = Clean(meta.MeetingLocation);
        meta.ZoomJoinUrl = Clean(meta.ZoomJoinUrl);
        meta.MeetingTime = NormalizeMeetingTime(meta.MeetingTime);
        meta.LastContactChannel = Clean(meta.LastContactChannel);
        meta.AddressLine = Clean(meta.AddressLine);
        meta.City = Clean(meta.City);
        meta.State = Clean(meta.State);
        meta.County = Clean(meta.County);
        meta.ZipCode = Clean(meta.ZipCode);
        meta.MortgageLender = Clean(meta.MortgageLender);
        meta.LoanAmount = Clean(meta.LoanAmount);
        meta.Gender = Clean(meta.Gender);
        if (meta.DOB.HasValue)
            meta.DOB = meta.DOB.Value.Date;
        meta.CrmPriority = NormalizePriority(meta.CrmPriority);
        meta.ContactStatus = NormalizeContactStatus(meta.ContactStatus);
        if (meta.CrmNextDate.HasValue)
            meta.CrmNextDate = meta.CrmNextDate.Value.Date;
        meta.CrmNextText = Clean(meta.CrmNextText);
        meta.CrmTags = Clean(meta.CrmTags);
        meta.AgentNotes = Clean(meta.AgentNotes);
        if (meta.AttemptsLifetime < 0)
            meta.AttemptsLifetime = 0;

        if (meta.MeetingDurationMinutes <= 0)
            meta.MeetingDurationMinutes = 30;

        meta.DocChecklist ??= new ClientCrmDocChecklist();
        meta.OpportunityPlanning ??= new ClientCrmOpportunityPlanningChecklist();
        meta.Collaboration ??= new ClientCrmCollaboration();
        meta.Collaboration.Owner = Clean(meta.Collaboration.Owner);
        meta.Collaboration.Watchers ??= new List<string>();
        meta.Collaboration.Watchers = meta.Collaboration.Watchers
            .Select(Clean)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(x => x)
            .ToList();
        meta.Collaboration.MentionNotes ??= new List<ClientCrmMentionNote>();

        foreach (var mention in meta.Collaboration.MentionNotes)
        {
            mention.Id = string.IsNullOrWhiteSpace(mention.Id) ? Guid.NewGuid().ToString("N") : mention.Id.Trim();
            mention.Note = Clean(mention.Note) ?? "";
            mention.MentionedUser = Clean(mention.MentionedUser);
            mention.CreatedBy = Clean(mention.CreatedBy);
            if (mention.CreatedUtc == default)
                mention.CreatedUtc = DateTime.UtcNow;
        }

        meta.Collaboration.MentionNotes = meta.Collaboration.MentionNotes
            .Where(x => !string.IsNullOrWhiteSpace(x.Note))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

        meta.Activities ??= new List<ClientCrmActivity>();

        foreach (var activity in meta.Activities)
        {
            activity.Id = string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id.Trim();
            activity.Type = Clean(activity.Type) ?? "Note";
            activity.Date = Clean(activity.Date) ?? "";
            activity.Note = Clean(activity.Note) ?? "";
            activity.Location = Clean(activity.Location);
            activity.MeetingLink = Clean(activity.MeetingLink);
            activity.CalendarEventId = Clean(activity.CalendarEventId);
            activity.CalendarWebLink = Clean(activity.CalendarWebLink);
            activity.OutcomeCode = Clean(activity.OutcomeCode);
            activity.Channel = Clean(activity.Channel);
            activity.CreatedBy = Clean(activity.CreatedBy);
            if (activity.CreatedUtc == default)
                activity.CreatedUtc = DateTime.UtcNow;
        }

        meta.Activities = meta.Activities
            .OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.CreatedUtc)
            .ToList();
    }

    public static string NormalizePipelineStage(string? stage)
    {
        var value = Clean(stage);
        if (string.IsNullOrWhiteSpace(value))
            return ClientCrmMeta.DefaultPipelineStage;

        if (value.Equals("Lead", StringComparison.OrdinalIgnoreCase))
            return ClientCrmMeta.DefaultPipelineStage;

        if (value.Equals("ClosedWon", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Placed Business", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Business Client", StringComparison.OrdinalIgnoreCase))
            return "BusinessClient";

        var match = AllowedPipelineStages.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? ClientCrmMeta.DefaultPipelineStage;
    }

    public static string NormalizeRecordType(string? recordType, bool defaultToLead = true)
    {
        var value = Clean(recordType);
        if (string.IsNullOrWhiteSpace(value))
            return defaultToLead ? "Lead" : "";

        if (value.Equals("Business Client", StringComparison.OrdinalIgnoreCase))
            return "BusinessClient";

        var match = AllowedRecordTypes.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? (defaultToLead ? "Lead" : "");
    }

    public static string NormalizeWaitingOn(string? value)
    {
        var cleaned = Clean(value);
        if (string.IsNullOrWhiteSpace(cleaned))
            return ClientCrmMeta.DefaultWaitingOn;

        var match = AllowedWaitingOnStates.FirstOrDefault(x => x.Equals(cleaned, StringComparison.OrdinalIgnoreCase));
        return match ?? ClientCrmMeta.DefaultWaitingOn;
    }

    public static string NormalizeContactStatus(string? value)
    {
        var cleaned = Clean(value);
        if (string.IsNullOrWhiteSpace(cleaned))
            return "NotSet";

        var match = AllowedContactStatuses.FirstOrDefault(x => x.Equals(cleaned, StringComparison.OrdinalIgnoreCase));
        return match ?? "NotSet";
    }

    private static string NormalizePriority(string? value)
    {
        var cleaned = Clean(value);
        if (string.IsNullOrWhiteSpace(cleaned))
            return "Normal";

        if (cleaned.Equals("Low", StringComparison.OrdinalIgnoreCase)) return "Low";
        if (cleaned.Equals("High", StringComparison.OrdinalIgnoreCase)) return "High";
        if (cleaned.Equals("Urgent", StringComparison.OrdinalIgnoreCase)) return "Urgent";
        return "Normal";
    }

    private static string? NormalizeMeetingTime(string? time)
    {
        var value = Clean(time);
        if (string.IsNullOrWhiteSpace(value))
            return "09:00";

        return TimeOnly.TryParse(value, out var parsed)
            ? parsed.ToString("HH:mm")
            : "09:00";
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
