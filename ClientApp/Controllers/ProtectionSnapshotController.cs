using ClientApp.Models;
using ClientApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.ClientExperience;
using System.Text.Json;

namespace ClientApp.Controllers;

[Authorize]
public sealed class ProtectionSnapshotController : Controller
{
    private readonly EffectiveClientContextService _clientContext;

    public ProtectionSnapshotController(EffectiveClientContextService clientContext)
    {
        _clientContext = clientContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var context = await _clientContext.ResolveAsync(User, Request.Cookies);
        if (context == null)
            return Forbid();

        var profile = context.Profile;
        var displayName = $"{profile.FirstName} {profile.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "Client";

        return View(new ProtectionSnapshotViewModel
        {
            ClientProfileId = context.ClientProfileId,
            ClientUserId = context.ClientUserId,
            ClientDisplayName = displayName,
            MaritalStatus = profile.MaritalStatus ?? string.Empty,
            Age = CalculateAge(profile.DOB),
            IsAgentView = context.IsAgentView,
            IsBusinessClient = IsBusinessClient(profile.CrmNotes),
            DefaultState = BuildDefaultState(profile.DOB, profile.MaritalStatus)
        });
    }

    private static bool IsBusinessClient(string? crmNotes)
    {
        if (string.IsNullOrWhiteSpace(crmNotes))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(crmNotes);
            if (doc.RootElement.TryGetProperty("recordType", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = (prop.GetString() ?? string.Empty).Trim();
                return value.Equals("BusinessClient", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("Business Client", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // ignore parse errors and fall through to false
        }

        return false;
    }

    private static ProtectionSnapshotState BuildDefaultState(DateTime? dob, string? maritalStatus)
    {
        var age = CalculateAge(dob);
        var isMarried = string.Equals((maritalStatus ?? string.Empty).Trim(), "Married", StringComparison.OrdinalIgnoreCase);

        return new ProtectionSnapshotState
        {
            HouseholdStage = age switch
            {
                < 35 => "Foundation",
                < 50 => "Growth",
                < 65 => "Protection Peak",
                _ => "Legacy"
            },
            PrimaryGoal = age >= 55 ? "Preserve assets" : "Protect income",
            EmergencyFundMonths = isMarried ? 4 : 3,
            IncomeProtectionYears = age >= 55 ? 5 : 10,
            HousingStatus = "Own",
            ReviewCadence = age >= 55 ? "Quarterly" : "Semiannual",
            PriorityFocusAreas = age >= 55
                ? new List<string> { "Legacy planning", "Beneficiary review" }
                : new List<string> { "Income protection", "Emergency preparedness" },
            ProtectionNeeds = new List<string>
            {
                "Life",
                "Home",
                "Auto",
                "Mortgage Protection",
                "Will",
                "Trust"
            }
        };
    }

    private static int? CalculateAge(DateTime? dob)
    {
        if (!dob.HasValue)
            return null;

        var today = DateTime.UtcNow.Date;
        var age = today.Year - dob.Value.Year;
        if (dob.Value.Date > today.AddYears(-age))
            age--;

        return age > 0 ? age : null;
    }
}