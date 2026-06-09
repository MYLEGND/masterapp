using System;

namespace Domain.Entities;

public class WorkstationLeadProfile
{
    public string LeadId { get; set; } = "";
    public string AgentUserId { get; set; } = "";
    public string Bucket { get; set; } = "MortgageProtection";
    public string? OriginalLeadType { get; set; } = "MortgageProtection";

    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Phone2 { get; set; }

    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? County { get; set; }
    public string? ZipCode { get; set; }
    public string? Age { get; set; }
    public DateTime? DOB { get; set; }
    public string? Gender { get; set; }
    public string? MortgageLender { get; set; }
    public string? LoanAmount { get; set; }
    public string? Btc { get; set; }

    public string CrmStatus { get; set; } = "Lead";
    public string CrmStage { get; set; } = "New";
    public long CrmOrder { get; set; }
    public string? CrmNotes { get; set; }

    public int CallCount { get; set; }
    public int CallsToday { get; set; }
    public int CallsWeek { get; set; }
    public int CallsMonth { get; set; }
    public int CallsYear { get; set; }
    public DateTime? CallsTodayDateUtc { get; set; }
    public DateTime? CallsWeekStartUtc { get; set; }
    public DateTime? CallsMonthStartUtc { get; set; }
    public DateTime? CallsYearStartUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
