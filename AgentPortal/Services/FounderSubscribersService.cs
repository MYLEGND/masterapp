using System.Globalization;
using System.Security.Claims;
using AgentPortal.Models;
using AgentPortal.Security;
using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Read-only Founder reporting over the shared billing authority. This service never
/// mutates subscriptions, payment records, offers, invitations, or entitlements.
/// </summary>
public sealed class FounderSubscribersService
{
    private const int PricingGroupPageSize = 24;
    private const int SubscriberPageSize = 25;

    private readonly MasterAppDbContext _db;
    private readonly ILogger<FounderSubscribersService> _logger;

    public FounderSubscribersService(MasterAppDbContext db, ILogger<FounderSubscribersService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FounderSubscribersDashboardVm> GetDashboardAsync(
        ClaimsPrincipal user,
        FounderSubscribersQuery? query,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);

        var normalizedQuery = NormalizeQuery(query);
        var snapshot = await LoadSnapshotAsync(cancellationToken);
        var filtered = Filter(snapshot.Records, normalizedQuery).ToList();
        var groupRows = BuildPricingGroups(filtered, snapshot.NowUtc);
        var groupPageCount = Math.Max(1, (int)Math.Ceiling(groupRows.Count / (double)PricingGroupPageSize));
        var groupPage = Math.Clamp(normalizedQuery.GroupPage, 1, groupPageCount);

        return new FounderSubscribersDashboardVm
        {
            Metrics = BuildMetrics(snapshot.Records, snapshot.Payments, snapshot.NowUtc),
            Query = normalizedQuery,
            PricingGroups = groupRows
                .Skip((groupPage - 1) * PricingGroupPageSize)
                .Take(PricingGroupPageSize)
                .ToList(),
            RevenueByAmount = BuildRevenueByAmount(snapshot.Records),
            SubscribersByAgent = BuildSubscribersByAgent(snapshot.Records),
            SubscribersByStatus = BuildSubscribersByStatus(snapshot.Records),
            RevenueByAgent = BuildRevenueByAgent(snapshot.Records),
            Agents = snapshot.Records
                .Where(x => !string.IsNullOrWhiteSpace(x.AgentUserId))
                .GroupBy(x => x.AgentUserId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FounderSelectOptionVm
                {
                    Value = group.Key,
                    Label = group.Select(x => x.AgentOwner).FirstOrDefault() ?? group.Key
                })
                .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Amounts = snapshot.Records
                .GroupBy(x => new { x.MonthlyAmountCents, x.Currency })
                .OrderByDescending(x => x.Key.MonthlyAmountCents)
                .Select(x => new FounderSelectOptionVm
                {
                    Value = x.Key.MonthlyAmountCents.ToString(CultureInfo.InvariantCulture),
                    Label = FormatMonthlyAmount(x.Key.MonthlyAmountCents, x.Key.Currency)
                })
                .ToList(),
            GroupPage = groupPage,
            GroupPageCount = groupPageCount,
            TotalGroupCount = groupRows.Count
        };
    }

    public async Task<FounderSubscriberGroupDetailVm?> GetPricingGroupAsync(
        ClaimsPrincipal user,
        int monthlyAmountCents,
        string? currency,
        FounderSubscribersQuery? query,
        int page,
        CancellationToken cancellationToken = default)
    {
        var normalizedCurrency = NormalizeCurrency(currency);
        var result = await GetSubscriberRowsAsync(
            user,
            query,
            page,
            rows => rows.Where(x => x.MonthlyAmountCents == monthlyAmountCents &&
                                    string.Equals(x.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

        return result.TotalCount == 0 ? null : result;
    }

    public Task<FounderSubscriberGroupDetailVm> GetCancelledSubscribersAsync(
        ClaimsPrincipal user,
        int page,
        CancellationToken cancellationToken = default)
    {
        return GetSubscriberRowsAsync(
            user,
            new FounderSubscribersQuery
            {
                Status = "Cancelled",
                ShowCancelled = true,
                Sort = "newest"
            },
            page,
            rows => rows,
            cancellationToken);
    }

    private async Task<FounderSubscriberGroupDetailVm> GetSubscriberRowsAsync(
        ClaimsPrincipal user,
        FounderSubscribersQuery? query,
        int page,
        Func<IEnumerable<FounderSubscriberRecord>, IEnumerable<FounderSubscriberRecord>> scope,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(user);

        var normalizedQuery = NormalizeQuery(query);
        var snapshot = await LoadSnapshotAsync(cancellationToken);
        var rows = scope(Filter(snapshot.Records, normalizedQuery)).ToList();
        var pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)SubscriberPageSize));
        var currentPage = Math.Clamp(page, 1, pageCount);
        var ordered = OrderRows(rows, normalizedQuery.Sort);

        return new FounderSubscriberGroupDetailVm
        {
            Subscribers = ordered
                .Skip((currentPage - 1) * SubscriberPageSize)
                .Take(SubscriberPageSize)
                .Select(x => ToRowVm(x, snapshot.NowUtc))
                .ToList(),
            Page = currentPage,
            PageCount = pageCount,
            TotalCount = rows.Count
        };
    }

    public async Task<FounderSubscriberClientContext?> ResolveClientContextAsync(
        ClaimsPrincipal user,
        Guid clientProfileId,
        string? agentUserId,
        CancellationToken cancellationToken = default)
    {
        FounderGuard.EnsureFounderOrThrow(user);

        var normalizedAgentId = NormalizeText(agentUserId);
        if (clientProfileId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedAgentId))
            return null;

        var normalizedAgentIdLower = normalizedAgentId.ToLowerInvariant();
        var context = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == clientProfileId)
            .Select(profile => new
            {
                profile.ClientUserId,
                IsAuthorized = _db.ClientSubscriptions.AsNoTracking().Any(subscription =>
                    subscription.ClientProfileId == profile.Id &&
                    subscription.OwnerAgentUserId.ToLower() == normalizedAgentIdLower) ||
                    (from invitation in _db.SubscriptionActivationInvitations.AsNoTracking()
                     join offer in _db.ClientSubscriptionOffers.AsNoTracking() on invitation.ClientSubscriptionOfferId equals offer.Id
                     where invitation.ClientProfileId == profile.Id && offer.OwnerAgentUserId.ToLower() == normalizedAgentIdLower
                     select invitation.Id).Any()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (context is null || !context.IsAuthorized || string.IsNullOrWhiteSpace(context.ClientUserId))
            return null;

        return new FounderSubscriberClientContext(context.ClientUserId.Trim(), normalizedAgentId);
    }

    private async Task<FounderSubscriberSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        var subscriptionSourceRows = await (
            from subscription in _db.ClientSubscriptions.AsNoTracking()
            join client in _db.ClientProfiles.AsNoTracking() on subscription.ClientProfileId equals client.Id
            join agentProfile in _db.AgentProfiles.AsNoTracking() on subscription.OwnerAgentUserId equals agentProfile.AgentUserId into agentProfiles
            from agentProfile in agentProfiles.DefaultIfEmpty()
            select new
            {
                SubscriptionId = subscription.Id,
                subscription.ClientProfileId,
                client.ClientUserId,
                client.FirstName,
                client.LastName,
                client.Email,
                client.Phone,
                client.CrmNotes,
                AgentName = agentProfile == null ? null : agentProfile.FullName,
                AgentUpn = agentProfile == null ? null : agentProfile.AgentUpn,
                subscription.OwnerAgentUserId,
                subscription.MonthlyAmountCents,
                subscription.Currency,
                subscription.Status,
                subscription.PaymentStanding,
                subscription.CreatedUtc,
                subscription.ActivatedUtc,
                subscription.NextBillingDateUtc,
                subscription.CancelledUtc,
                subscription.EndedUtc,
                subscription.Provider,
                subscription.ProviderPlanVariationId,
                subscription.IsPlatformManaged,
                ClientAppStatus = _db.ClientEntitlements
                    .AsNoTracking()
                    .Where(entitlement => entitlement.ClientProfileId == client.Id && entitlement.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess)
                    .OrderByDescending(entitlement => entitlement.UpdatedUtc)
                    .Select(entitlement => (ClientEntitlementStatus?)entitlement.Status)
                    .FirstOrDefault()
            }).ToListAsync(cancellationToken);

        var subscriptionRows = subscriptionSourceRows.Select(row => new FounderSubscriberRecord
        {
            RecordId = row.SubscriptionId.ToString(),
            ClientProfileId = row.ClientProfileId,
            ClientUserId = row.ClientUserId,
            Customer = BuildCustomerName(row.FirstName, row.LastName, row.Email),
            AgentOwner = BuildAgentName(row.AgentName, row.AgentUpn, row.OwnerAgentUserId),
            AgentUserId = row.OwnerAgentUserId,
            MonthlyAmountCents = row.MonthlyAmountCents,
            Currency = row.Currency,
            Status = MapSubscriptionStatus(row.Status, row.PaymentStanding),
            IsCancelled = row.Status == ClientSubscriptionStatus.Canceled,
            CreatedUtc = row.CreatedUtc,
            StartedUtc = row.ActivatedUtc ?? row.CreatedUtc,
            RenewalUtc = row.NextBillingDateUtc,
            CancelledUtc = row.CancelledUtc ?? row.EndedUtc,
            Provider = row.Provider.ToString(),
            CurrentPlan = string.IsNullOrWhiteSpace(row.ProviderPlanVariationId)
                ? (row.IsPlatformManaged ? "Platform-managed subscription" : "Subscription")
                : row.ProviderPlanVariationId,
            ClientAppStatus = row.ClientAppStatus?.ToString(),
            Email = row.Email,
            Phone = row.Phone,
            CrmSearchText = row.CrmNotes,
            SubscriptionId = row.SubscriptionId
        }).ToList();

        var invitationSourceRows = await (
            from invitation in _db.SubscriptionActivationInvitations.AsNoTracking()
            join offer in _db.ClientSubscriptionOffers.AsNoTracking() on invitation.ClientSubscriptionOfferId equals offer.Id
            join client in _db.ClientProfiles.AsNoTracking() on invitation.ClientProfileId equals client.Id
            join agentProfile in _db.AgentProfiles.AsNoTracking() on offer.OwnerAgentUserId equals agentProfile.AgentUserId into agentProfiles
            from agentProfile in agentProfiles.DefaultIfEmpty()
            where invitation.Status != SubscriptionActivationInvitationStatus.Redeemed &&
                  invitation.Status != SubscriptionActivationInvitationStatus.Revoked &&
                  invitation.Status != SubscriptionActivationInvitationStatus.Superseded &&
                  !_db.ClientSubscriptions.Any(subscription => subscription.AcceptedOfferId == offer.Id)
            select new
            {
                InvitationId = invitation.Id,
                invitation.ClientProfileId,
                client.ClientUserId,
                client.FirstName,
                client.LastName,
                client.Email,
                client.Phone,
                client.CrmNotes,
                AgentName = agentProfile == null ? null : agentProfile.FullName,
                AgentUpn = agentProfile == null ? null : agentProfile.AgentUpn,
                offer.OwnerAgentUserId,
                offer.MonthlyAmountCents,
                offer.Currency,
                InvitationStatus = invitation.Status,
                invitation.CreatedUtc,
                ClientAppStatus = _db.ClientEntitlements
                    .AsNoTracking()
                    .Where(entitlement => entitlement.ClientProfileId == client.Id && entitlement.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess)
                    .OrderByDescending(entitlement => entitlement.UpdatedUtc)
                    .Select(entitlement => (ClientEntitlementStatus?)entitlement.Status)
                    .FirstOrDefault()
            }).ToListAsync(cancellationToken);

        var invitationRows = invitationSourceRows.Select(row => new FounderSubscriberRecord
        {
            RecordId = row.InvitationId.ToString(),
            ClientProfileId = row.ClientProfileId,
            ClientUserId = row.ClientUserId,
            Customer = BuildCustomerName(row.FirstName, row.LastName, row.Email),
            AgentOwner = BuildAgentName(row.AgentName, row.AgentUpn, row.OwnerAgentUserId),
            AgentUserId = row.OwnerAgentUserId,
            MonthlyAmountCents = row.MonthlyAmountCents,
            Currency = row.Currency,
            Status = MapInvitationStatus(row.InvitationStatus),
            CreatedUtc = row.CreatedUtc,
            Provider = "—",
            CurrentPlan = "Activation invitation",
            ClientAppStatus = row.ClientAppStatus?.ToString(),
            Email = row.Email,
            Phone = row.Phone,
            CrmSearchText = row.CrmNotes
        }).ToList();

        var paymentRows = await _db.SubscriptionPayments
            .AsNoTracking()
            .Where(payment => payment.ClientSubscriptionId != null && payment.Kind != SubscriptionPaymentKind.CommerceOneTime)
            .Select(payment => new FounderPaymentRecord
            {
                SubscriptionId = payment.ClientSubscriptionId!.Value,
                AmountCents = payment.AmountCents,
                Currency = payment.Currency,
                Status = payment.Status,
                OccurredUtc = payment.ProviderOccurredUtc ?? payment.UpdatedUtc
            })
            .ToListAsync(cancellationToken);

        var paymentBySubscription = BuildPaymentSummaries(paymentRows);
        foreach (var record in subscriptionRows)
        {
            if (record.SubscriptionId.HasValue && paymentBySubscription.TryGetValue(record.SubscriptionId.Value, out var payment))
                record.Payment = payment;
        }

        var records = subscriptionRows.Concat(invitationRows).ToList();
        _logger.LogInformation("Founder subscriber snapshot loaded. Subscriptions={SubscriptionCount} Invitations={InvitationCount} Payments={PaymentCount}", subscriptionRows.Count, invitationRows.Count, paymentRows.Count);
        return new FounderSubscriberSnapshot(records, paymentRows, nowUtc);
    }

    private static FounderSubscribersQuery NormalizeQuery(FounderSubscribersQuery? raw)
    {
        return new FounderSubscribersQuery
        {
            Search = NormalizeText(raw?.Search),
            Status = NormalizeText(raw?.Status),
            AgentId = NormalizeText(raw?.AgentId),
            MonthlyAmountCents = raw?.MonthlyAmountCents,
            CreatedFromUtc = raw?.CreatedFromUtc?.Date,
            CreatedToUtc = raw?.CreatedToUtc?.Date,
            RenewalFromUtc = raw?.RenewalFromUtc?.Date,
            RenewalToUtc = raw?.RenewalToUtc?.Date,
            ShowCancelled = raw?.ShowCancelled ?? false,
            Sort = NormalizeSort(raw?.Sort),
            GroupPage = Math.Max(1, raw?.GroupPage ?? 1)
        };
    }

    private static IEnumerable<FounderSubscriberRecord> Filter(IEnumerable<FounderSubscriberRecord> rows, FounderSubscribersQuery query)
    {
        var filtered = rows;

        if (!query.ShowCancelled)
            filtered = filtered.Where(row => !row.IsCancelled);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var needle = query.Search;
            filtered = filtered.Where(row => MatchesSearch(row, needle));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
            filtered = filtered.Where(row => row.Status.Equals(query.Status, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.AgentId))
            filtered = filtered.Where(row => row.AgentUserId.Equals(query.AgentId, StringComparison.OrdinalIgnoreCase));

        if (query.MonthlyAmountCents.HasValue)
            filtered = filtered.Where(row => row.MonthlyAmountCents == query.MonthlyAmountCents.Value);

        if (query.CreatedFromUtc.HasValue)
            filtered = filtered.Where(row => row.CreatedUtc >= query.CreatedFromUtc.Value);

        if (query.CreatedToUtc.HasValue)
            filtered = filtered.Where(row => row.CreatedUtc < query.CreatedToUtc.Value.AddDays(1));

        if (query.RenewalFromUtc.HasValue)
            filtered = filtered.Where(row => row.RenewalUtc.HasValue && row.RenewalUtc.Value >= query.RenewalFromUtc.Value);

        if (query.RenewalToUtc.HasValue)
            filtered = filtered.Where(row => row.RenewalUtc.HasValue && row.RenewalUtc.Value < query.RenewalToUtc.Value.AddDays(1));

        return filtered;
    }

    private static bool MatchesSearch(FounderSubscriberRecord row, string needle)
    {
        return Contains(row.Customer, needle) ||
               Contains(row.AgentOwner, needle) ||
               Contains(row.Email, needle) ||
               Contains(row.Phone, needle) ||
               Contains(row.RecordId, needle) ||
               Contains(row.ClientUserId, needle) ||
               Contains(row.Status, needle) ||
               Contains(row.CrmSearchText, needle) ||
               Contains(FormatMonthlyAmount(row.MonthlyAmountCents, row.Currency), needle) ||
               row.MonthlyAmountCents.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static List<FounderPricingGroupVm> BuildPricingGroups(IReadOnlyCollection<FounderSubscriberRecord> rows, DateTime nowUtc)
    {
        var activeSubscriberCount = rows.Count(row => row.Status == "Active");
        var activeRevenueByCurrency = rows
            .Where(row => row.Status == "Active")
            .GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.MonthlyAmountCents), StringComparer.OrdinalIgnoreCase);
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return rows
            .GroupBy(row => new { Amount = row.MonthlyAmountCents, Currency = NormalizeCurrency(row.Currency) })
            .OrderByDescending(group => group.Key.Amount)
            .ThenBy(group => group.Key.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var all = group.ToList();
                var active = all.Where(row => row.Status == "Active").ToList();
                var successes = all.Sum(row => row.Payment.SuccessCount);
                var failures = all.Sum(row => row.Payment.FailureCount);
                var activatedThisMonth = all.Count(row => row.StartedUtc.HasValue && row.StartedUtc.Value >= monthStart);
                var cancelledThisMonth = all.Count(row => row.CancelledUtc.HasValue && row.CancelledUtc.Value >= monthStart);
                var currencyRevenue = activeRevenueByCurrency.GetValueOrDefault(group.Key.Currency);
                var monthlyRevenue = active.Sum(row => row.MonthlyAmountCents);
                var lifetimeDays = all
                    .Where(row => row.StartedUtc.HasValue)
                    .Select(row => (Math.Max(0, ((row.CancelledUtc ?? nowUtc) - row.StartedUtc!.Value).TotalDays)))
                    .ToList();

                return new FounderPricingGroupVm
                {
                    MonthlyAmountCents = group.Key.Amount,
                    Currency = group.Key.Currency,
                    AmountLabel = FormatMonthlyAmount(group.Key.Amount, group.Key.Currency),
                    SubscriberCount = all.Count,
                    MonthlyRevenue = FormatMoney(monthlyRevenue, group.Key.Currency),
                    AnnualRevenue = FormatMoney(monthlyRevenue * 12, group.Key.Currency),
                    RevenueShare = FormatPercentage(currencyRevenue == 0 ? null : monthlyRevenue / (double)currencyRevenue),
                    SubscriberShare = FormatPercentage(activeSubscriberCount == 0 ? null : active.Count / (double)activeSubscriberCount),
                    NewestSubscriber = all.OrderByDescending(row => row.StartedUtc ?? row.CreatedUtc).Select(row => row.Customer).FirstOrDefault() ?? "—",
                    OldestSubscriber = all.OrderBy(row => row.StartedUtc ?? row.CreatedUtc).Select(row => row.Customer).FirstOrDefault() ?? "—",
                    AverageLifetime = lifetimeDays.Count == 0 ? "—" : FormatLifetime(lifetimeDays.Average()),
                    CollectionSuccessRate = FormatPercentage((successes + failures) == 0 ? null : successes / (double)(successes + failures)),
                    CancelledCount = all.Count(row => row.IsCancelled),
                    GrowthSinceLastMonth = activatedThisMonth - cancelledThisMonth,
                    NetGainLoss = activatedThisMonth - cancelledThisMonth
                };
            })
            .ToList();
    }

    private static FounderSubscriberMetricsVm BuildMetrics(
        IReadOnlyCollection<FounderSubscriberRecord> allRows,
        IReadOnlyCollection<FounderPaymentRecord> payments,
        DateTime nowUtc)
    {
        var subscriptions = allRows.Where(row => row.SubscriptionId.HasValue).ToList();
        var active = subscriptions.Where(row => row.Status == "Active").ToList();
        var cancelled = subscriptions.Where(row => row.IsCancelled).ToList();
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var activeRevenue = active.GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase);
        var newSubscribers = subscriptions.Count(row => row.StartedUtc.HasValue && row.StartedUtc.Value >= monthStart);
        var lostThisMonth = cancelled.Where(row => row.CancelledUtc.HasValue && row.CancelledUtc.Value >= monthStart).ToList();
        var openingBase = subscriptions.Count(row => row.StartedUtc.HasValue && row.StartedUtc.Value < monthStart &&
                                                    (!row.CancelledUtc.HasValue || row.CancelledUtc.Value >= monthStart));
        var successfulPayments = payments.Count(payment => payment.Status == SubscriptionPaymentStatus.Completed);
        var failedPayments = payments.Count(payment => payment.Status == SubscriptionPaymentStatus.Failed);
        var recoveredRevenue = CalculateRecoveredRevenue(payments, monthStart);

        return new FounderSubscriberMetricsVm
        {
            LastUpdatedUtc = nowUtc,
            TotalActiveRevenue = FormatCurrencyTotals(activeRevenue.Select(group => new CurrencyTotal(group.Key, group.Sum(row => row.MonthlyAmountCents)))),
            ActiveSubscribers = active.Count,
            CancelledSubscribers = cancelled.Count,
            MonthlyRecurringRevenue = FormatCurrencyTotals(activeRevenue.Select(group => new CurrencyTotal(group.Key, group.Sum(row => row.MonthlyAmountCents)))),
            AverageRevenuePerSubscriber = FormatAverage(active),
            HighestSubscription = FormatExtremum(active, highest: true),
            LowestSubscription = FormatExtremum(active, highest: false),
            NewSubscribersThisMonth = newSubscribers,
            LostSubscribersThisMonth = lostThisMonth.Count,
            RetentionRate = FormatPercentage(openingBase == 0 ? null : 1d - lostThisMonth.Count / (double)openingBase),
            ChurnRate = FormatPercentage(openingBase == 0 ? null : lostThisMonth.Count / (double)openingBase),
            CollectionSuccessRate = FormatPercentage((successfulPayments + failedPayments) == 0 ? null : successfulPayments / (double)(successfulPayments + failedPayments)),
            PaymentFailureRate = FormatPercentage((successfulPayments + failedPayments) == 0 ? null : failedPayments / (double)(successfulPayments + failedPayments)),
            LostRevenue = FormatCurrencyTotals(lostThisMonth
                .GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase)
                .Select(group => new CurrencyTotal(group.Key, group.Sum(row => row.MonthlyAmountCents)))),
            RecoveredRevenue = FormatCurrencyTotals(recoveredRevenue),
            AverageSubscription = FormatAverage(active),
            MedianSubscription = FormatMedian(active),
            LargestSubscription = FormatExtremum(active, highest: true),
            SmallestSubscription = FormatExtremum(active, highest: false)
        };
    }

    private static IReadOnlyList<FounderDistributionVm> BuildRevenueByAmount(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        return rows.Where(row => row.Status == "Active")
            .GroupBy(row => new { row.MonthlyAmountCents, Currency = NormalizeCurrency(row.Currency) })
            .OrderByDescending(group => group.Key.MonthlyAmountCents)
            .Select(group => new FounderDistributionVm
            {
                Label = FormatMonthlyAmount(group.Key.MonthlyAmountCents, group.Key.Currency),
                Value = FormatMoney(group.Sum(row => row.MonthlyAmountCents), group.Key.Currency),
                Detail = $"{group.Count():N0} active subscriber{(group.Count() == 1 ? string.Empty : "s")}" 
            })
            .ToList();
    }

    private static IReadOnlyList<FounderDistributionVm> BuildSubscribersByAgent(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        return rows.GroupBy(row => row.AgentOwner, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FounderDistributionVm
            {
                Label = group.Key,
                Value = group.Count().ToString("N0", CultureInfo.InvariantCulture),
                Detail = "subscription records"
            })
            .ToList();
    }

    private static IReadOnlyList<FounderDistributionVm> BuildSubscribersByStatus(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        return rows.GroupBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FounderDistributionVm
            {
                Label = group.Key,
                Value = group.Count().ToString("N0", CultureInfo.InvariantCulture),
                Detail = "subscription records"
            })
            .ToList();
    }

    private static IReadOnlyList<FounderDistributionVm> BuildRevenueByAgent(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        return rows.Where(row => row.Status == "Active")
            .GroupBy(row => new { row.AgentOwner, Currency = NormalizeCurrency(row.Currency) })
            .OrderByDescending(group => group.Sum(row => row.MonthlyAmountCents))
            .ThenBy(group => group.Key.AgentOwner, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FounderDistributionVm
            {
                Label = group.Key.AgentOwner,
                Value = FormatMoney(group.Sum(row => row.MonthlyAmountCents), group.Key.Currency),
                Detail = $"{group.Count():N0} active subscriber{(group.Count() == 1 ? string.Empty : "s")}" 
            })
            .ToList();
    }

    private static IEnumerable<FounderSubscriberRecord> OrderRows(IEnumerable<FounderSubscriberRecord> rows, string? sort)
    {
        return NormalizeSort(sort) switch
        {
            "newest" => rows.OrderByDescending(row => row.StartedUtc ?? row.CreatedUtc).ThenBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            "oldest" => rows.OrderBy(row => row.StartedUtc ?? row.CreatedUtc).ThenBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            "lifetime" => rows.OrderByDescending(row => row.Payment.LifetimeValueCents).ThenBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            "renewal" => rows.OrderBy(row => row.RenewalUtc ?? DateTime.MaxValue).ThenBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            "subscribers" => rows.OrderBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            "alphabetical" => rows.OrderBy(row => row.Customer, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(row => row.MonthlyAmountCents).ThenBy(row => row.Customer, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static FounderSubscriberRowVm ToRowVm(FounderSubscriberRecord record, DateTime nowUtc)
    {
        var cancellationDate = record.CancelledUtc;
        var daysSinceCancellation = cancellationDate.HasValue
            ? Math.Max(0, (int)Math.Floor((nowUtc - cancellationDate.Value).TotalDays)).ToString(CultureInfo.InvariantCulture)
            : "—";

        return new FounderSubscriberRowVm
        {
            RecordId = record.RecordId,
            ClientProfileId = record.ClientProfileId,
            ClientUserId = record.ClientUserId,
            Customer = record.Customer,
            AgentOwner = record.AgentOwner,
            AgentUserId = record.AgentUserId,
            SubscriptionAmount = FormatMonthlyAmount(record.MonthlyAmountCents, record.Currency),
            Status = record.Status,
            StatusTone = GetStatusTone(record.Status),
            StartedUtc = record.StartedUtc,
            RenewalUtc = record.RenewalUtc,
            LifetimeValue = FormatMoney(record.Payment.LifetimeValueCents, record.Currency),
            MonthsActive = record.StartedUtc.HasValue ? FormatLifetime(Math.Max(0, ((record.CancelledUtc ?? nowUtc) - record.StartedUtc.Value).TotalDays)) : "—",
            LastSuccessfulPaymentUtc = record.Payment.LastSuccessUtc,
            LastFailedPaymentUtc = record.Payment.LastFailureUtc,
            PaymentProvider = record.Provider,
            CurrentPlan = record.CurrentPlan,
            ClientAppStatus = HumanizeClientAppStatus(record.ClientAppStatus),
            Email = EmptyAsDash(record.Email),
            Phone = EmptyAsDash(record.Phone),
            CancelledUtc = cancellationDate,
            LastAgent = record.AgentOwner,
            DaysSinceCancellation = daysSinceCancellation,
            PotentialWinBack = record.IsCancelled ? "Requires eligibility review" : "—"
        };
    }

    private static Dictionary<Guid, FounderPaymentSummary> BuildPaymentSummaries(IEnumerable<FounderPaymentRecord> payments)
    {
        var summaries = new Dictionary<Guid, FounderPaymentSummary>();
        foreach (var group in payments.GroupBy(payment => payment.SubscriptionId))
        {
            var summary = new FounderPaymentSummary();
            foreach (var payment in group.OrderBy(payment => payment.OccurredUtc))
            {
                if (payment.Status == SubscriptionPaymentStatus.Completed)
                {
                    summary.LifetimeValueCents += payment.AmountCents;
                    summary.SuccessCount++;
                    summary.LastSuccessUtc = payment.OccurredUtc;
                }
                else if (payment.Status == SubscriptionPaymentStatus.Failed)
                {
                    summary.FailureCount++;
                    summary.LastFailureUtc = payment.OccurredUtc;
                }
            }

            summaries[group.Key] = summary;
        }

        return summaries;
    }

    private static IReadOnlyList<CurrencyTotal> CalculateRecoveredRevenue(IEnumerable<FounderPaymentRecord> payments, DateTime monthStart)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var subscriptionPayments in payments.GroupBy(payment => payment.SubscriptionId))
        {
            var unresolvedFailure = false;
            foreach (var payment in subscriptionPayments.OrderBy(payment => payment.OccurredUtc))
            {
                if (payment.Status == SubscriptionPaymentStatus.Failed)
                {
                    unresolvedFailure = true;
                    continue;
                }

                if (payment.Status == SubscriptionPaymentStatus.Completed)
                {
                    if (unresolvedFailure && payment.OccurredUtc >= monthStart)
                    {
                        var currency = NormalizeCurrency(payment.Currency);
                        totals[currency] = totals.GetValueOrDefault(currency) + payment.AmountCents;
                    }

                    unresolvedFailure = false;
                }
            }
        }

        return totals.Select(pair => new CurrencyTotal(pair.Key, pair.Value)).ToList();
    }

    private static string MapSubscriptionStatus(ClientSubscriptionStatus status, ClientSubscriptionPaymentStanding paymentStanding)
    {
        if (paymentStanding == ClientSubscriptionPaymentStanding.Failed || status is ClientSubscriptionStatus.ActivationFailed or ClientSubscriptionStatus.Suspended)
            return "Payment Failed";

        return status switch
        {
            ClientSubscriptionStatus.Active => "Active",
            ClientSubscriptionStatus.PastDue or ClientSubscriptionStatus.GracePeriod => "Past Due",
            ClientSubscriptionStatus.Paused => "Paused",
            ClientSubscriptionStatus.Canceled => "Cancelled",
            ClientSubscriptionStatus.AwaitingPaymentMethod or ClientSubscriptionStatus.PendingProviderActivation or ClientSubscriptionStatus.Draft or ClientSubscriptionStatus.ReconciliationRequired => "Pending Activation",
            _ => "Pending Activation"
        };
    }

    private static string MapInvitationStatus(SubscriptionActivationInvitationStatus status) => status switch
    {
        SubscriptionActivationInvitationStatus.Expired => "Expired",
        SubscriptionActivationInvitationStatus.Sent or SubscriptionActivationInvitationStatus.Viewed or SubscriptionActivationInvitationStatus.PaymentStarted => "Invitation Sent",
        _ => "Pending Activation"
    };

    private static string GetStatusTone(string status) => status switch
    {
        "Active" => "success",
        "Past Due" or "Payment Failed" => "danger",
        "Paused" => "warning",
        "Cancelled" or "Expired" => "muted",
        "Invitation Sent" => "info",
        _ => "pending"
    };

    private static string FormatExtremum(IReadOnlyCollection<FounderSubscriberRecord> rows, bool highest)
    {
        if (rows.Count == 0) return "—";
        var currencyGroups = rows.GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase).ToList();
        if (currencyGroups.Count != 1) return "Multiple currencies";
        var value = highest ? rows.Max(row => row.MonthlyAmountCents) : rows.Min(row => row.MonthlyAmountCents);
        return FormatMonthlyAmount(value, currencyGroups[0].Key);
    }

    private static string FormatAverage(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        if (rows.Count == 0) return "—";
        return FormatCurrencyTotals(rows.GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase)
            .Select(group => new CurrencyTotal(group.Key, (int)Math.Round(group.Average(row => row.MonthlyAmountCents), MidpointRounding.AwayFromZero))));
    }

    private static string FormatMedian(IReadOnlyCollection<FounderSubscriberRecord> rows)
    {
        if (rows.Count == 0) return "—";
        var groups = rows.GroupBy(row => NormalizeCurrency(row.Currency), StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count != 1) return "Multiple currencies";
        var values = groups[0].OrderBy(row => row.MonthlyAmountCents).Select(row => row.MonthlyAmountCents).ToList();
        var midpoint = values.Count / 2;
        var median = values.Count % 2 == 0
            ? (int)Math.Round((values[midpoint - 1] + values[midpoint]) / 2d, MidpointRounding.AwayFromZero)
            : values[midpoint];
        return FormatMonthlyAmount(median, groups[0].Key);
    }

    private static string FormatCurrencyTotals(IEnumerable<CurrencyTotal> totals)
    {
        var materialized = totals.Where(total => total.Cents != 0).ToList();
        if (materialized.Count == 0) return "$0.00";
        return string.Join(" · ", materialized.OrderBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(total => FormatMoney(total.Cents, total.Currency)));
    }

    private static string FormatMoney(int cents, string? currency)
    {
        var normalizedCurrency = NormalizeCurrency(currency);
        var value = cents / 100m;
        return string.Equals(normalizedCurrency, "USD", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"{normalizedCurrency} {value:N2}";
    }

    private static string FormatMonthlyAmount(int cents, string? currency) => $"{FormatMoney(cents, currency)}/mo";

    private static string FormatPercentage(double? value) => value.HasValue ? value.Value.ToString("P1", CultureInfo.InvariantCulture) : "—";

    private static string FormatLifetime(double days)
    {
        var months = days / 30.4375d;
        return months < 1d ? $"{Math.Round(days):N0} days" : $"{months:N1} months";
    }

    private static string HumanizeClientAppStatus(string? status) => string.IsNullOrWhiteSpace(status)
        ? "Not granted"
        : status switch
        {
            "NotGranted" => "Not granted",
            "GracePeriod" => "Grace period",
            _ => status
        };

    private static string BuildCustomerName(string? firstName, string? lastName, string? email)
    {
        var name = $"{firstName} {lastName}".Trim();
        return !string.IsNullOrWhiteSpace(name) ? name : EmptyAsDash(email, "Client");
    }

    private static string BuildAgentName(string? fullName, string? upn, string? agentUserId)
    {
        return !string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : EmptyAsDash(upn, EmptyAsDash(agentUserId, "Unassigned"));
    }

    private static string EmptyAsDash(string? value, string fallback = "—") => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();
    private static string NormalizeCurrency(string? value) => string.IsNullOrWhiteSpace(value) ? "USD" : value.Trim().ToUpperInvariant();
    private static string NormalizeSort(string? value) => (value ?? "revenue").Trim().ToLowerInvariant();
    private static bool Contains(string? value, string needle) => !string.IsNullOrWhiteSpace(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private sealed class FounderSubscriberSnapshot
    {
        public FounderSubscriberSnapshot(IReadOnlyList<FounderSubscriberRecord> records, IReadOnlyList<FounderPaymentRecord> payments, DateTime nowUtc)
        {
            Records = records;
            Payments = payments;
            NowUtc = nowUtc;
        }

        public IReadOnlyList<FounderSubscriberRecord> Records { get; }
        public IReadOnlyList<FounderPaymentRecord> Payments { get; }
        public DateTime NowUtc { get; }
    }

    private sealed class FounderSubscriberRecord
    {
        public string RecordId { get; init; } = string.Empty;
        public Guid ClientProfileId { get; init; }
        public string ClientUserId { get; init; } = string.Empty;
        public string Customer { get; init; } = "Client";
        public string AgentOwner { get; init; } = "Unassigned";
        public string AgentUserId { get; init; } = string.Empty;
        public int MonthlyAmountCents { get; init; }
        public string Currency { get; init; } = "USD";
        public string Status { get; init; } = "Pending Activation";
        public bool IsCancelled { get; init; }
        public DateTime CreatedUtc { get; init; }
        public DateTime? StartedUtc { get; init; }
        public DateTime? RenewalUtc { get; init; }
        public DateTime? CancelledUtc { get; init; }
        public string Provider { get; init; } = "—";
        public string CurrentPlan { get; init; } = "—";
        public string? ClientAppStatus { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string? CrmSearchText { get; init; }
        public Guid? SubscriptionId { get; init; }
        public FounderPaymentSummary Payment { get; set; } = new();
    }

    private sealed class FounderPaymentRecord
    {
        public Guid SubscriptionId { get; init; }
        public int AmountCents { get; init; }
        public string Currency { get; init; } = "USD";
        public SubscriptionPaymentStatus Status { get; init; }
        public DateTime OccurredUtc { get; init; }
    }

    private sealed class FounderPaymentSummary
    {
        public int LifetimeValueCents { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
    }

    private sealed record CurrencyTotal(string Currency, int Cents);
}

public sealed record FounderSubscriberClientContext(string ClientUserId, string AgentUserId);
