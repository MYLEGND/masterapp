using System;

namespace AgentPortal.Models;

public class WorkstationLeadViewModel
{
    public string LeadId { get; set; } = "";
    public string Bucket { get; set; } = "";
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
    public DateTime? DOB { get; set; }
    public string? Gender { get; set; }
    public string? MortgageLender { get; set; }
    public string? LoanAmount { get; set; }
    public string? OriginalLeadType { get; set; } = "";
    public string CrmStage { get; set; } = "";
    public string CrmStatus { get; set; } = "";
    public string? CrmNotes { get; set; }
    public int CallCount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PersonalAmount { get; set; }
}
