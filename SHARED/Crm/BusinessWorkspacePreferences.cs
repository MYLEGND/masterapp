using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Shared.Crm;

// Persisted on the existing business storefront settings, shared by both portals.
public sealed class BusinessWorkspacePreferences
{
    public string LeadLabel { get; set; } = "Leads";
    public string ClientLabel { get; set; } = "Clients";
    public List<string> Stages { get; set; } = ["New", "Contacted", "Qualified", "Proposal", "Won", "Closed"];
    public List<string> Metrics { get; set; } = ["visitors", "sessions", "leads", "conversion"];
    public Guid? NotificationMemberId { get; set; }
    public Guid? NotificationAgentTrackingProfileId { get; set; }
    public static readonly string[] AvailableMetrics = ["visitors", "sessions", "pageviews", "leads", "conversion"];
    public static BusinessWorkspacePreferences Read(string? json) =>
        JsonSerializer.Deserialize<BusinessWorkspacePreferences>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new();
    public string Write()
    {
        LeadLabel = Label(LeadLabel, 40);
        ClientLabel = Label(ClientLabel, 40);
        if (Stages is null || Stages.Count is < 1 or > 20) throw new ValidationException("Choose between 1 and 20 stages.");
        Stages = Stages.Select(x => Label(x, 60)).ToList();
        if (Stages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Stages.Count) throw new ValidationException("Stage names must be unique.");
        if (Metrics is null || Metrics.Count == 0 || Metrics.Any(x => !AvailableMetrics.Contains(x))) throw new ValidationException("Select at least one supported metric.");
        Metrics = Metrics.Distinct().ToList();
        if (NotificationMemberId.HasValue && NotificationAgentTrackingProfileId.HasValue) throw new ValidationException("Choose one intake recipient.");
        return JsonSerializer.Serialize(this);
    }
    private static string Label(string? value, int max)
    {
        var result = value?.Trim() ?? "";
        if (result.Length is < 1 || result.Length > max || result.Any(char.IsControl)) throw new ValidationException($"Labels must contain 1–{max} characters.");
        return result;
    }
}

public sealed class BusinessWorkspaceSettingsInput
{
    public Guid Revision { get; set; }
    [Required, MaxLength(40)] public string LeadLabel { get; set; } = "Leads";
    [Required, MaxLength(40)] public string ClientLabel { get; set; } = "Clients";
    [Required, MaxLength(1240)] public string Stages { get; set; } = "New\nContacted\nQualified\nProposal\nWon\nClosed";
    public List<string> Metrics { get; set; } = [];
    [MaxLength(50)] public string? Recipient { get; set; }
}

public sealed record BusinessIntakeRecipient(string Key, string Label, string Email);
