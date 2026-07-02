using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public sealed class ParfaitBusinessPlatformConsoleViewModel
{
    public bool IsPlatformOwner { get; set; }
    public string OwnerEmailSummary { get; set; } = string.Empty;
    public List<ParfaitBusinessAccountCardViewModel> Businesses { get; set; } = [];
    public ParfaitBusinessCreateInput NewBusiness { get; set; } = new();
}

public sealed class ParfaitBusinessAccountCardViewModel
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public string BusinessStatus { get; set; } = string.Empty;
    public string SubscriptionPlan { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty;
    public int Products { get; set; }
    public int Orders { get; set; }
    public int Members { get; set; }
}

public sealed class ParfaitBusinessCreateInput
{
    [Required]
    [MaxLength(80)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Use lowercase letters, numbers, and hyphens only.")]
    public string Key { get; set; } = string.Empty;

    [Required]
    [MaxLength(160)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string LegalName { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string BusinessType { get; set; } = "Ecommerce";

    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string OwnerEmail { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? PrimaryDomain { get; set; }

    [Required]
    [MaxLength(80)]
    public string PlanKey { get; set; } = "starter";
}
