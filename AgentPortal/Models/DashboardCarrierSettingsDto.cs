using System.Text.Json.Serialization;

namespace AgentPortal.Models;

public sealed class DashboardCarrierSettingsDto
{
    public List<DashboardCarrierSettingItemDto> Items { get; set; } = new();

    public DateTime? SavedUtc { get; set; }
}

public sealed class DashboardCarrierSettingItemDto
{
    public string EntryKey { get; set; } = string.Empty;

    public string CategoryKey { get; set; } = string.Empty;

    public string CategoryName { get; set; } = string.Empty;

    public string CarrierKey { get; set; } = string.Empty;

    public string CarrierName { get; set; } = string.Empty;

    public string AgentNumber { get; set; } = string.Empty;

    public string EntityNumber { get; set; } = string.Empty;

    [JsonPropertyName("producerNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProducerNumberLegacy
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(EntityNumber))
                EntityNumber = value ?? string.Empty;
        }
    }

    public string Notes { get; set; } = string.Empty;

    public List<DashboardCarrierCompensationLineDto> CompensationLines { get; set; } = new();
}

public sealed class DashboardCarrierCompensationLineDto
{
    public string ProductLine { get; set; } = string.Empty;

    public string CommissionPercent { get; set; } = string.Empty;

    public string EligibilityNotes { get; set; } = string.Empty;
}
