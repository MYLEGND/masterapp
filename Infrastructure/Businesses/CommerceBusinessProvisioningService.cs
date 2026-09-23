using System.Text.Json;
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
    Task UpdateEntityNameAsync(Guid businessId, Guid actorClientProfileId, string entityName,
        CancellationToken cancellationToken = default, string? actorAgentUserId = null);
    Task UpdateOwnershipAsync(Guid businessId, Guid actorClientProfileId, string entityName,
        IReadOnlyList<BusinessOwnerInput> owners, CancellationToken cancellationToken = default, string? actorAgentUserId = null);
    Task<BusinessOwnerAccount?> ResolveOwnerAsync(string email, string? actorAgentUserId = null,
        CancellationToken cancellationToken = default);
    Task<CommerceBusiness> CreateAsync(
        CommerceBusinessProvisioningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BusinessOwnerInput(Guid? ClientProfileId, string Email, decimal Percentage);
public sealed record BusinessOwnerAccount(Guid ClientProfileId, string Email, string FirstName, string LastName);

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
    CommerceBusinessStorefrontProvisioning? Storefront = null,
    Guid? OwnerClientProfileId = null,
    IReadOnlyList<BusinessOwnerInput>? Owners = null,
    string? ActorAgentUserId = null);

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

        var linkedOwners = request.Owners is { Count: > 0 }
            ? await ResolveOwnersAsync(request.Owners, request.ActorAgentUserId, cancellationToken) : null;
        var primaryIsOwner = string.Equals(roleKey, "owner", StringComparison.OrdinalIgnoreCase);
        if (linkedOwners != null && primaryIsOwner &&
            !linkedOwners.Any(x => x.Profile.Id == request.OwnerClientProfileId))
            throw new InvalidOperationException("The primary personal account must remain a linked owner.");
        if (linkedOwners != null && !primaryIsOwner && request.OwnerClientProfileId.HasValue &&
            linkedOwners.Any(x => x.Profile.Id == request.OwnerClientProfileId.Value))
            throw new InvalidOperationException("The business profile account must remain separate from linked personal owners.");
        _db.CommerceBusinesses.Add(business);
        var primaryMember = new CommerceBusinessMember
        {
            ClientProfileId = request.OwnerClientProfileId,
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
            UpdatedUtc = now,
            OwnershipPercentage = primaryIsOwner && linkedOwners != null
                ? linkedOwners.Single(x => x.Profile.Id == request.OwnerClientProfileId).Percentage
                : null
        };
        _db.CommerceBusinessMembers.Add(primaryMember);
        if (linkedOwners != null)
            foreach (var owner in linkedOwners.Where(x => !primaryIsOwner || x.Profile.Id != request.OwnerClientProfileId))
                _db.CommerceBusinessMembers.Add(OwnerMember(business, owner.Profile, owner.Percentage, now));

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

    public async Task UpdateEntityNameAsync(Guid businessId, Guid actorClientProfileId, string entityName,
        CancellationToken cancellationToken = default, string? actorAgentUserId = null)
    {
        var name = Required(entityName, 160, "Entity name");
        var business = await _db.CommerceBusinesses.SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Business is not available.");
        if (!await _db.CommerceBusinessMembers.AnyAsync(x => x.CommerceBusinessId == businessId &&
            x.ClientProfileId == actorClientProfileId && x.Status == "Active" && x.CanManageTeam &&
            (x.RoleKey == "owner" || x.RoleKey == "account"), cancellationToken))
            throw new UnauthorizedAccessException("Business management is not permitted.");
        if (!string.IsNullOrWhiteSpace(actorAgentUserId) &&
            !await Infrastructure.WebsiteEditing.WebsiteBusinessAccess.CanManageAsActorAsync(
                _db, businessId, actorClientProfileId, actorAgentUserId, cancellationToken: cancellationToken))
            throw new UnauthorizedAccessException("Business management is outside the agent's client scope.");
        var history = string.IsNullOrWhiteSpace(business.OwnershipHistoryJson)
            ? new List<JsonElement>() : JsonSerializer.Deserialize<List<JsonElement>>(business.OwnershipHistoryJson)!;
        history.Add(JsonSerializer.SerializeToElement(new
        {
            ChangedUtc = DateTime.UtcNow, ActorClientProfileId = actorClientProfileId, ActorAgentUserId = actorAgentUserId,
            PreviousEntityName = business.DisplayName, EntityName = name
        }));
        business.OwnershipHistoryJson = JsonSerializer.Serialize(history);
        ApplyEntityName(business, name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyEntityName(CommerceBusiness business, string entityName)
    {
        var legalNameFollowsEntity = string.IsNullOrWhiteSpace(business.LegalName) || business.LegalName == business.DisplayName;
        business.DisplayName = Required(entityName, 160, "Entity name");
        if (legalNameFollowsEntity) business.LegalName = business.DisplayName;
        business.UpdatedUtc = DateTime.UtcNow;
    }

    public async Task UpdateOwnershipAsync(Guid businessId, Guid actorClientProfileId, string entityName,
        IReadOnlyList<BusinessOwnerInput> owners, CancellationToken cancellationToken = default, string? actorAgentUserId = null)
    {
        var business = await _db.CommerceBusinesses.SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Business is not available.");
        var members = await _db.CommerceBusinessMembers.Where(x => x.CommerceBusinessId == businessId).ToListAsync(cancellationToken);
        var actorMember = members.SingleOrDefault(x => x.ClientProfileId == actorClientProfileId && x.Status == "Active" &&
            x.CanManageTeam && (x.RoleKey == "owner" || x.RoleKey == "account"));
        if (actorMember is null)
            throw new UnauthorizedAccessException("Business ownership management is not permitted.");
        var resolved = await ResolveOwnersAsync(owners, actorAgentUserId, cancellationToken);
        foreach (var owner in resolved)
        {
            if (members.Any(m => m.ClientProfileId == owner.Profile.Id && m.RoleKey != "owner"))
                throw new InvalidOperationException("An existing non-owner membership must be reconciled before changing its role.");
        }
        if (actorMember.RoleKey == "owner" && !resolved.Any(x => x.Profile.Id == actorClientProfileId))
            throw new InvalidOperationException("Keep your personal account linked while updating ownership.");
        if (actorMember.RoleKey == "account" && resolved.Any(x => x.Profile.Id == actorClientProfileId))
            throw new InvalidOperationException("The business profile account must remain separate from linked personal owners.");
        var history = string.IsNullOrWhiteSpace(business.OwnershipHistoryJson)
            ? new List<JsonElement>() : JsonSerializer.Deserialize<List<JsonElement>>(business.OwnershipHistoryJson)!;
        history.Add(JsonSerializer.SerializeToElement(new
        {
            ChangedUtc = DateTime.UtcNow, ActorClientProfileId = actorClientProfileId, ActorAgentUserId = actorAgentUserId,
            PreviousEntityName = business.DisplayName, EntityName = entityName.Trim(),
            PreviousOwners = members.Where(x => x.Status == "Active" && x.RoleKey == "owner").Select(x => new { x.ClientProfileId, x.OwnershipPercentage }).ToArray(),
            Owners = resolved.Select(x => new { ClientProfileId = x.Profile.Id, OwnershipPercentage = x.Percentage }).ToArray()
        }));
        business.OwnershipHistoryJson = JsonSerializer.Serialize(history);
        ApplyEntityName(business, entityName);
        foreach (var member in members.Where(x => x.RoleKey == "owner"))
        {
            var owner = resolved.SingleOrDefault(x => x.Profile.Id == member.ClientProfileId);
            member.UpdatedUtc = business.UpdatedUtc;
            if (owner.Profile == null) { member.Status = "Revoked"; continue; }
            member.OwnershipPercentage = owner.Percentage;
            member.Email = owner.Profile.Email.Trim();
            member.NormalizedEmail = member.Email.ToUpperInvariant();
            member.DisplayName = $"{owner.Profile.FirstName} {owner.Profile.LastName}".Trim();
            member.Status = "Active";
        }
        foreach (var owner in resolved.Where(x => !members.Any(m => m.ClientProfileId == x.Profile.Id)))
        {
            if (members.Any(m => m.NormalizedEmail == owner.Profile.Email.Trim().ToUpperInvariant()))
                throw new InvalidOperationException("An existing membership must be reconciled before linking that personal account.");
            _db.CommerceBusinessMembers.Add(OwnerMember(business, owner.Profile, owner.Percentage, business.UpdatedUtc));
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BusinessOwnerAccount?> ResolveOwnerAsync(string email, string? actorAgentUserId = null,
        CancellationToken cancellationToken = default)
    {
        ClientProfile? profile;
        try
        {
            profile = await ResolveOwnerProfileAsync(email, null, actorAgentUserId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return profile is null
            ? null
            : new BusinessOwnerAccount(profile.Id, profile.Email.Trim(), profile.FirstName ?? string.Empty, profile.LastName ?? string.Empty);
    }

    private async Task<List<(ClientProfile Profile, decimal Percentage)>> ResolveOwnersAsync(
        IReadOnlyList<BusinessOwnerInput> owners, string? actorAgentUserId, CancellationToken ct)
    {
        if (owners.Count == 0 || owners.Count > 20 ||
            owners.Any(x => x.Percentage <= 0 || x.Percentage > 100 || decimal.Round(x.Percentage, 2) != x.Percentage) ||
            owners.Sum(x => x.Percentage) != 100)
            throw new InvalidOperationException("Owner shares must be greater than zero and total exactly 100%.");

        var result = new List<(ClientProfile Profile, decimal Percentage)>();
        foreach (var owner in owners)
        {
            var profile = await ResolveOwnerProfileAsync(owner.Email, owner.ClientProfileId, actorAgentUserId, ct)
                ?? throw new InvalidOperationException("Each owner must identify one existing personal client account with matching ID and email.");
            if (result.Any(x => x.Profile.Id == profile.Id))
                throw new InvalidOperationException("A personal account cannot be listed twice.");
            result.Add((profile, owner.Percentage));
        }
        return result;
    }

    private async Task<ClientProfile?> ResolveOwnerProfileAsync(
        string email, Guid? clientProfileId, string? actorAgentUserId, CancellationToken ct)
    {
        var normalizedEmail = Email(email);
        var candidates = await _db.ClientProfiles.Where(x => x.Email.ToLower() == normalizedEmail &&
            (!clientProfileId.HasValue || x.Id == clientProfileId.Value)).Take(2).ToListAsync(ct);
        if (candidates.Count != 1 || string.IsNullOrWhiteSpace(candidates[0].ClientUserId) ||
            ClientRecordClassification.Resolve(candidates[0].ClientUserId, candidates[0].CrmNotes) == ClientRecordClassification.Lead)
            return null;

        var profile = candidates[0];
        if (!string.IsNullOrWhiteSpace(actorAgentUserId) &&
            !await _db.AgentOwnsClientAsync(actorAgentUserId, profile.ClientUserId, ct: ct))
            return null;

        return profile;
    }

    private static CommerceBusinessMember OwnerMember(CommerceBusiness business, ClientProfile profile, decimal percentage, DateTime now) => new()
    {
        CommerceBusiness = business, ClientProfileId = profile.Id, Email = profile.Email.Trim(),
        NormalizedEmail = profile.Email.Trim().ToUpperInvariant(), DisplayName = $"{profile.FirstName} {profile.LastName}".Trim(),
        RoleKey = "owner", Status = "Active", OwnershipPercentage = percentage,
        CanManageStorefront = true, CanManageAnalytics = true, CanManageTeam = true,
        CanManageCatalog = false, CanManageOrders = false, CreatedUtc = now, UpdatedUtc = now
    };

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
