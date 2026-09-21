using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Businesses;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using ParfaitApp.Security;

namespace ParfaitApp.Services;

public interface IParfaitBusinessPlatformService
{
    Task<ParfaitBusinessPlatformConsoleViewModel> GetConsoleAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task CreateBusinessAsync(ClaimsPrincipal user, ParfaitBusinessCreateInput input, CancellationToken ct = default);
    Task EnsureParfaitPlatformRecordsAsync(CancellationToken ct = default);
}

public sealed class ParfaitBusinessPlatformService : IParfaitBusinessPlatformService
{
    private readonly MasterAppDbContext _db;
    private readonly ICommerceBusinessProvisioningService _businessProvisioning;

    public ParfaitBusinessPlatformService(
        MasterAppDbContext db,
        ICommerceBusinessProvisioningService businessProvisioning)
    {
        _db = db;
        _businessProvisioning = businessProvisioning;
    }

    public async Task<ParfaitBusinessPlatformConsoleViewModel> GetConsoleAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        EnsurePlatformOwner(user);

        await EnsureParfaitPlatformRecordsAsync(ct);

        var businesses = await _db.CommerceBusinesses
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        var ids = businesses.Select(x => x.Id).ToList();

        var subscriptions = await _db.CommerceBusinessSubscriptions
            .AsNoTracking()
            .Where(x => ids.Contains(x.CommerceBusinessId))
            .ToDictionaryAsync(x => x.CommerceBusinessId, ct);

        var productCounts = await _db.CommerceProducts
            .AsNoTracking()
            .Where(x => ids.Contains(x.CommerceBusinessId))
            .GroupBy(x => x.CommerceBusinessId)
            .Select(x => new { BusinessId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.BusinessId, x => x.Count, ct);

        var orderCounts = await _db.CommerceOrders
            .AsNoTracking()
            .Where(x => ids.Contains(x.CommerceBusinessId))
            .GroupBy(x => x.CommerceBusinessId)
            .Select(x => new { BusinessId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.BusinessId, x => x.Count, ct);

        var memberCounts = await _db.CommerceBusinessMembers
            .AsNoTracking()
            .Where(x => ids.Contains(x.CommerceBusinessId))
            .GroupBy(x => x.CommerceBusinessId)
            .Select(x => new { BusinessId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.BusinessId, x => x.Count, ct);

        return new ParfaitBusinessPlatformConsoleViewModel
        {
            IsPlatformOwner = true,
            OwnerEmailSummary = ParfaitFounderGuard.OwnerEmailSummary(),
            Businesses = businesses.Select(business =>
            {
                subscriptions.TryGetValue(business.Id, out var subscription);
                return new ParfaitBusinessAccountCardViewModel
                {
                    Id = business.Id,
                    Key = business.Key,
                    DisplayName = business.DisplayName,
                    LegalName = business.LegalName,
                    BusinessType = business.BusinessType,
                    OwnerEmail = business.OwnerEmail,
                    PrimaryDomain = business.PrimaryDomain ?? "",
                    BusinessStatus = business.Status,
                    SubscriptionPlan = subscription?.PlanName ?? "Unassigned",
                    SubscriptionStatus = subscription?.Status ?? "Missing",
                    Products = productCounts.GetValueOrDefault(business.Id),
                    Orders = orderCounts.GetValueOrDefault(business.Id),
                    Members = memberCounts.GetValueOrDefault(business.Id)
                };
            }).ToList()
        };
    }

    public async Task CreateBusinessAsync(ClaimsPrincipal user, ParfaitBusinessCreateInput input, CancellationToken ct = default)
    {
        EnsurePlatformOwner(user);

        var normalizedPlan = NormalizePlan(input.PlanKey);
        await _businessProvisioning.CreateAsync(
            new CommerceBusinessProvisioningRequest(
                DisplayName: input.DisplayName,
                LegalName: input.LegalName,
                BusinessType: input.BusinessType,
                OwnerEmail: input.OwnerEmail,
                Key: input.Key,
                PrimaryDomain: input.PrimaryDomain,
                OwnerDisplayName: input.OwnerEmail,
                Subscription: new CommerceBusinessSubscriptionProvisioning(
                    normalizedPlan.Key,
                    normalizedPlan.Name,
                    "Trial",
                    normalizedPlan.MonthlyPriceCents,
                    "Manual",
                    DateTime.UtcNow.AddDays(14)),
                Storefront: new CommerceBusinessStorefrontProvisioning(
                    input.DisplayName,
                    $"{input.DisplayName.Trim()} storefront.",
                    "Draft")),
            ct);
    }

    public async Task EnsureParfaitPlatformRecordsAsync(CancellationToken ct = default)
    {
        var parfait = await _db.CommerceBusinesses
            .SingleOrDefaultAsync(x => x.Key == ParfaitBusinessScopeService.ParfaitBusinessKey, ct);

        if (parfait is null)
            return;

        var changed = false;

        if (!await _db.CommerceBusinessSubscriptions.AnyAsync(x => x.CommerceBusinessId == parfait.Id, ct))
        {
            _db.CommerceBusinessSubscriptions.Add(new CommerceBusinessSubscription
            {
                CommerceBusinessId = parfait.Id,
                PlanKey = "platform-owned",
                PlanName = "Platform Owned",
                Status = "Active",
                MonthlyPriceCents = 0,
                BillingProvider = "Internal"
            });
            changed = true;
        }

        if (!await _db.CommerceBusinessStorefrontSettings.AnyAsync(x => x.CommerceBusinessId == parfait.Id, ct))
        {
            _db.CommerceBusinessStorefrontSettings.Add(new CommerceBusinessStorefrontSettings
            {
                CommerceBusinessId = parfait.Id,
                BrandHeadline = "Parfait",
                BrandSubheadline = "Performance apparel and wellness commerce.",
                StorefrontStatus = "Active"
            });
            changed = true;
        }

        foreach (var email in ParfaitFounderGuard.OwnerEmails)
        {
            var normalized = NormalizeEmail(email);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (await _db.CommerceBusinessMembers.AnyAsync(x =>
                    x.CommerceBusinessId == parfait.Id &&
                    x.NormalizedEmail == normalized.ToUpperInvariant(), ct))
            {
                continue;
            }

            _db.CommerceBusinessMembers.Add(new CommerceBusinessMember
            {
                CommerceBusinessId = parfait.Id,
                Email = normalized,
                NormalizedEmail = normalized.ToUpperInvariant(),
                DisplayName = normalized,
                RoleKey = "platform-owner",
                Status = "Active"
            });
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private static void EnsurePlatformOwner(ClaimsPrincipal user)
    {
        if (!ParfaitFounderGuard.IsFounder(user))
            throw new UnauthorizedAccessException("Only platform owners can manage business accounts.");
    }

    private static (string Key, string Name, int MonthlyPriceCents) NormalizePlan(string? planKey)
    {
        return (planKey ?? "").Trim().ToLowerInvariant() switch
        {
            "growth" => ("growth", "Growth", 9900),
            "scale" => ("scale", "Scale", 19900),
            _ => ("starter", "Starter", 4900)
        };
    }

    private static string NormalizeEmail(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

}
