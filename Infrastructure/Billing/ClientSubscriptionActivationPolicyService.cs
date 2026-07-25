using Domain.Billing;
using Domain.Entities;

namespace Infrastructure.Billing;

internal sealed class ClientSubscriptionActivationPolicyService : IClientSubscriptionActivationPolicyService
{
    private readonly ClientSubscriptionActivationPolicyOptions _options;
    private readonly TimeZoneInfo _businessTimeZone;

    public ClientSubscriptionActivationPolicyService(ClientSubscriptionActivationPolicyOptions options)
    {
        _options = options;
        _businessTimeZone = ResolveTimeZone(options.BusinessTimeZoneId);
    }

    public ClientSubscriptionActivationSchedule ResolveActivationSchedule(ClientSubscriptionOffer offer, DateTime nowUtc)
    {
        var effectiveNowUtc = EnsureUtc(nowUtc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(effectiveNowUtc, _businessTimeZone);
        var anchorDay = ResolveAnchorDay(offer);
        var firstChargeUtc = effectiveNowUtc;

        DateOnly firstRecurringRenewalLocalDate;
        if (anchorDay.HasValue)
        {
            firstRecurringRenewalLocalDate = ResolveAnchoredRenewalDate(localNow, anchorDay.Value, _options.SameDayAnchorCutoffHourLocal, _options.MinimumDaysBeforeAnchoredRenewal);
        }
        else
        {
            firstRecurringRenewalLocalDate = DateOnly.FromDateTime(localNow.Date.AddDays(_options.MinimumDaysBeforeAnchoredRenewal));
        }

        var firstRecurringRenewalUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(firstRecurringRenewalLocalDate.Year, firstRecurringRenewalLocalDate.Month, firstRecurringRenewalLocalDate.Day, 0, 0, 0, DateTimeKind.Unspecified),
            _businessTimeZone);

        return new ClientSubscriptionActivationSchedule(
            offer.MonthlyAmountCents,
            NormalizeCurrency(offer.Currency),
            anchorDay,
            _businessTimeZone.Id,
            _options.MinimumDaysBeforeAnchoredRenewal,
            _options.SameDayAnchorCutoffHourLocal,
            firstChargeUtc,
            firstRecurringRenewalUtc,
            firstRecurringRenewalLocalDate);
    }

    public ClientSubscriptionRenewalSchedule ResolveRenewalSchedule(ClientSubscription subscription)
    {
        var periodStartUtc = subscription.NextBillingDateUtc
            ?? subscription.CurrentPeriodEndUtc
            ?? throw new InvalidOperationException("An active subscription must have a next billing date before a renewal can be scheduled.");
        var localPeriodStart = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(periodStartUtc), ResolveTimeZone(subscription.BillingTimeZoneId));
        var renewalDate = subscription.BillingAnchorDay.HasValue
            ? BuildAnchoredDate(DateOnly.FromDateTime(localPeriodStart.Date).AddMonths(1), subscription.BillingAnchorDay.Value)
            : DateOnly.FromDateTime(localPeriodStart.Date.AddMonths(1));
        var timeZone = ResolveTimeZone(subscription.BillingTimeZoneId);
        var periodEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(renewalDate.Year, renewalDate.Month, renewalDate.Day, 0, 0, 0, DateTimeKind.Unspecified),
            timeZone);

        return new ClientSubscriptionRenewalSchedule(
            EnsureUtc(periodStartUtc),
            periodEndUtc,
            periodEndUtc);
    }

    public TimeSpan? ResolveRenewalRetryDelay(int failedAttemptNumber)
    {
        if (failedAttemptNumber <= 0 || failedAttemptNumber > _options.RenewalRetryDelayMinutes.Count)
            return null;

        return TimeSpan.FromMinutes(_options.RenewalRetryDelayMinutes[failedAttemptNumber - 1]);
    }

    public DateTime ResolveGracePeriodEndUtc(DateTime failureUtc)
    {
        return EnsureUtc(failureUtc).AddDays(_options.GracePeriodDays);
    }

    public int ResolveUpcomingRenewalReminderDays() => Math.Max(1, _options.UpcomingRenewalReminderDays);

    public int ResolveGracePeriodReminderDaysBeforeEnd() =>
        Math.Max(_options.GracePeriodFinalReminderDaysBeforeEnd, _options.GracePeriodReminderDaysBeforeEnd);

    public int ResolveGracePeriodFinalReminderDaysBeforeEnd() =>
        Math.Min(_options.GracePeriodFinalReminderDaysBeforeEnd, _options.GracePeriodReminderDaysBeforeEnd);

    private static int? ResolveAnchorDay(ClientSubscriptionOffer offer)
    {
        return offer.BillingAnchorSelectionMode switch
        {
            BillingAnchorSelectionMode.FirstOfMonth => 1,
            BillingAnchorSelectionMode.FifteenthOfMonth => 15,
            BillingAnchorSelectionMode.SpecificDayOfMonth => ClampAnchorDay(offer.SelectedBillingAnchorDay),
            _ => throw new InvalidOperationException("Unsupported billing anchor selection mode.")
        };
    }

    private static int? ClampAnchorDay(int? day)
    {
        return day is >= 1 and <= 31 ? day : null;
    }

    private static DateOnly ResolveAnchoredRenewalDate(DateTime localNow, int anchorDay, int cutoffHourLocal, int minimumIntervalDays)
    {
        var firstChargeDate = DateOnly.FromDateTime(localNow.Date);
        var candidate = BuildAnchoredDate(firstChargeDate, anchorDay);

        if (candidate < firstChargeDate || (candidate == firstChargeDate && localNow.Hour >= cutoffHourLocal))
            candidate = BuildAnchoredDate(firstChargeDate.AddMonths(1), anchorDay);

        while (candidate.DayNumber - firstChargeDate.DayNumber < minimumIntervalDays)
            candidate = BuildAnchoredDate(candidate.AddMonths(1), anchorDay);

        return candidate;
    }

    private static DateOnly BuildAnchoredDate(DateOnly referenceMonth, int anchorDay)
    {
        var daysInMonth = DateTime.DaysInMonth(referenceMonth.Year, referenceMonth.Month);
        return new DateOnly(referenceMonth.Year, referenceMonth.Month, Math.Min(anchorDay, daysInMonth));
    }

    private static TimeZoneInfo ResolveTimeZone(string configuredTimeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static string NormalizeCurrency(string currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
}
