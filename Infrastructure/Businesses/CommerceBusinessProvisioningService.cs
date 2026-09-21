using System.Text.RegularExpressions;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Businesses;

/// <summary>
/// The single business-tenant creation authority for CommerceBusiness.
/// Callers select capabilities and optional subscription details; they do not
/// create CommerceBusiness, owner membership, storefront settings, or subscription
/// rows independently.
/// </summary>
public interface ICommerceBusinessProvisioningService
{
    Task<CommerceBusiness> CreateAsync(
        CommerceBusinessProvisioningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CommerceBusinessProvisioningRequest(
    string DisplayName,
    string LegalName,
    string BusinessType,
    string OwnerEmail,
    string? Key = null,
    string? PrimaryDomain = null,
    string? OwnerDisplayName = null,
    string OwnerRoleKey = "owner",
    bool CanManageStorefront = true,
    bool CanManageCatalog = true,
    bool CanManageOrders = true,
    bool CanManageAnalytics = true,
    bool CanManageTeam = true,
    CommerceBusinessSubscriptionProvisioning? Subscription = null,
    CommerceBusinessStorefrontProvisioning? Storefront = null);

public sealed record CommerceBusinessSubscriptionProvisioning(
    string PlanKey,
    string PlanName,
    string Status,
    int MonthlyPriceCents,
    string BillingProvider,
    DateTime? TrialEndsUtc = null,
    DateTime? CurrentPeriodEndsUtc = null);

public sealed record CommerceBusinessStorefrontProvisioning(
    string BrandHeadline,
    string BrandSubheadline,
    string StorefrontStatus = "Draft",
    string AccentColor = "#926950",
    string LogoUrl = "");

public sealed class CommerceBusinessProvisioningService : ICommerceBusinessProvisioningService
{
    private static readonly Regex ExplicitKeyPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MasterAppDbContext _db;

    public CommerceBusinessProvisioningService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<CommerceBusiness> CreateAsync(
        CommerceBusinessProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var displayName = Required(request.DisplayName, 160, "Business display name");
        var legalName = Required(request.LegalName, 200, "Business legal name");
        var businessType = Required(request.BusinessType, 120, "Business type");
        var ownerEmail = Email(request.OwnerEmail);
        var primaryDomain = Optional(request.PrimaryDomain, 255);
        var ownerDisplayName = Optional(request.OwnerDisplayName, 160) ?? ownerEmail;
        var roleKey = Required(request.OwnerRoleKey, 80, "Owner role");
        var key = string.IsNullOrWhiteSpace(request.Key)
            ? await GenerateUniqueKeyAsync(displayName, cancellationToken)
            : NormalizeExplicitKey(request.Key);

        if (await _db.CommerceBusinesses.AsNoTracking().AnyAsync(
                business => business.Key == key,
                cancellationToken))
        {
            throw new InvalidOperationException("A business with that key already exists.");
        }

        var now = DateTime.UtcNow;
        var business = new CommerceBusiness
        {
            Key = key,
            DisplayName = displayName,
            LegalName = legalName,
            BusinessType = businessType,
            OwnerEmail = ownerEmail,
            PrimaryDomain = primaryDomain,
            Status = "Active",
            IsActive = true,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        _db.CommerceBusinesses.Add(business);
        _db.CommerceBusinessMembers.Add(new CommerceBusinessMember
        {
            CommerceBusiness = business,
            Email = ownerEmail,
            NormalizedEmail = ownerEmail.ToUpperInvariant(),
            DisplayName = ownerDisplayName,
            RoleKey = roleKey,
            Status = "Active",
            CanManageStorefront = request.CanManageStorefront,
            CanManageCatalog = request.CanManageCatalog,
            CanManageOrders = request.CanManageOrders,
            CanManageAnalytics = request.CanManageAnalytics,
            CanManageTeam = request.CanManageTeam,
            CreatedUtc = now,
            UpdatedUtc = now
        });

        var storefront = request.Storefront ??
            new CommerceBusinessStorefrontProvisioning(
                displayName,
                $"{displayName} storefront.");

        _db.CommerceBusinessStorefrontSettings.Add(new CommerceBusinessStorefrontSettings
        {
            CommerceBusiness = business,
            BrandHeadline = Required(storefront.BrandHeadline, 180, "Storefront headline"),
            BrandSubheadline = Required(storefront.BrandSubheadline, 300, "Storefront subheadline"),
            StorefrontStatus = Required(storefront.StorefrontStatus, 40, "Storefront status"),
            AccentColor = Required(storefront.AccentColor, 40, "Storefront accent color"),
            LogoUrl = Optional(storefront.LogoUrl, 2048) ?? string.Empty,
            UpdatedUtc = now
        });

        if (request.Subscription is { } subscription)
        {
            _db.CommerceBusinessSubscriptions.Add(new CommerceBusinessSubscription
            {
                CommerceBusiness = business,
                PlanKey = Required(subscription.PlanKey, 80, "Subscription plan key"),
                PlanName = Required(subscription.PlanName, 120, "Subscription plan name"),
                Status = Required(subscription.Status, 40, "Subscription status"),
                MonthlyPriceCents = subscription.MonthlyPriceCents,
                BillingProvider = Required(subscription.BillingProvider, 80, "Billing provider"),
                TrialEndsUtc = subscription.TrialEndsUtc,
                CurrentPeriodEndsUtc = subscription.CurrentPeriodEndsUtc,
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return business;
    }

    private async Task<string> GenerateUniqueKeyAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        var seed = Slugify(displayName);
        if (seed.Length == 0) seed = "business";
        if (seed.Length > 70) seed = seed[..70].Trim('-');

        var candidate = seed;
        var suffix = 2;
        while (await _db.CommerceBusinesses.AsNoTracking().AnyAsync(
                   business => business.Key == candidate,
                   cancellationToken))
        {
            var suffixText = "-" + suffix++;
            var prefixLength = Math.Min(seed.Length, 80 - suffixText.Length);
            candidate = seed[..prefixLength].TrimEnd('-') + suffixText;
        }

        return candidate;
    }

    private static string NormalizeExplicitKey(string value)
    {
        var key = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length is < 1 or > 80 || !ExplicitKeyPattern.IsMatch(key))
            throw new InvalidOperationException("Business key must use lowercase letters, numbers, and hyphens.");
        return key;
    }

    private static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character =>
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9')
                    ? character
                    : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private static string Required(string? value, int maxLength, string label)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length == 0 || cleaned.Length > maxLength)
            throw new InvalidOperationException($"{label} is required and must be {maxLength} characters or fewer.");
        return cleaned;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var cleaned = (value ?? string.Empty).Trim();
        if (cleaned.Length == 0) return null;
        if (cleaned.Length > maxLength)
            throw new InvalidOperationException($"Value must be {maxLength} characters or fewer.");
        return cleaned;
    }

    private static string Email(string? value)
    {
        var email = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length == 0 || email.Length > 320 || !email.Contains('@'))
            throw new InvalidOperationException("A valid owner email is required.");
        return email;
    }
}
