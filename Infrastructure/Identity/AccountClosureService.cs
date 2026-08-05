using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Identity;

public sealed record AccountClosureExecutionResult(
    bool Claimed,
    bool Closed,
    string? DeferredCode = null);

/// <summary>
/// The single executor for an already-authorized account closure request.
/// AccountLifecycleService owns the request; this service owns the durable,
/// retryable transition from DeletionRequested to Closed.
/// </summary>
public interface IAccountClosureService
{
    Task<int> ProcessPendingAsync(int take, CancellationToken cancellationToken = default);
    Task<AccountClosureExecutionResult> ProcessAsync(Guid accountLifecycleRecordId, CancellationToken cancellationToken = default);
}

public sealed class AccountClosureService : IAccountClosureService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private readonly MasterAppDbContext _db;
    private readonly IBillingOrchestrator _billing;
    private readonly IClientEntraLifecycleService _clientEntra;
    private readonly ISocialFeedService _social;
    private readonly ILogger<AccountClosureService> _logger;

    public AccountClosureService(
        MasterAppDbContext db,
        IBillingOrchestrator billing,
        IClientEntraLifecycleService clientEntra,
        ISocialFeedService social,
        ILogger<AccountClosureService> logger)
    {
        _db = db;
        _billing = billing;
        _clientEntra = clientEntra;
        _social = social;
        _logger = logger;
    }

    public async Task<int> ProcessPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

        var now = DateTime.UtcNow;
        var candidates = await _db.AccountLifecycleRecords
            .AsNoTracking()
            .Where(record => record.State == AccountLifecycleStates.DeletionRequested &&
                             (record.ClosureLeaseExpiresUtc == null || record.ClosureLeaseExpiresUtc <= now))
            .OrderBy(record => record.DeletionRequestedUtc)
            .ThenBy(record => record.Id)
            .Select(record => record.Id)
            .Take(take)
            .ToArrayAsync(cancellationToken);

        var closedCount = 0;
        foreach (var candidate in candidates)
        {
            var result = await ProcessAsync(candidate, cancellationToken);
            if (result.Closed)
                closedCount++;
        }

        return closedCount;
    }

    public async Task<AccountClosureExecutionResult> ProcessAsync(
        Guid accountLifecycleRecordId,
        CancellationToken cancellationToken = default)
    {
        if (accountLifecycleRecordId == Guid.Empty)
            throw new ArgumentException("An account lifecycle record is required.", nameof(accountLifecycleRecordId));

        var leaseId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var record = await _db.AccountLifecycleRecords.SingleOrDefaultAsync(
            item => item.Id == accountLifecycleRecordId,
            cancellationToken);
        if (record is null || record.State != AccountLifecycleStates.DeletionRequested ||
            (record.ClosureLeaseExpiresUtc is { } existingLease && existingLease > now))
        {
            return new AccountClosureExecutionResult(false, false);
        }

        record.ClosureLeaseId = leaseId;
        record.ClosureLeaseExpiresUtc = now.Add(LeaseDuration);
        record.ClosureAttemptCount++;
        record.LastClosureAttemptUtc = now;
        record.LastClosureErrorCode = null;
        record.UpdatedUtc = now;
        AddAudit(record, "closure_claimed", "started", now);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new AccountClosureExecutionResult(false, false);
        }

        try
        {
            var identityForms = await GetIdentityFormsAsync(record, cancellationToken);

            if (record.ParticipantType == MessagingParticipantTypes.Client)
            {
                var billingClosed = await CancelClientSubscriptionsAsync(record, cancellationToken);
                if (!billingClosed)
                {
                    return await DeferAsync(
                        record,
                        leaseId,
                        "account_closure_billing_incomplete",
                        cancellationToken);
                }
            }

            var social = await _social.RemoveAccountContentForClosureAsync(
                new SocialFeedActor(
                    new MessagingActor(record.UserId, record.ParticipantType),
                    record.ProfileId,
                    "Closed Legend account"),
                cancellationToken);
            if (!social.Succeeded)
            {
                return await DeferAsync(
                    record,
                    leaseId,
                    "account_closure_social_incomplete",
                    cancellationToken);
            }

            await DeactivatePushDevicesAsync(record, identityForms, cancellationToken);

            if (record.ParticipantType == MessagingParticipantTypes.Client)
            {
                await _clientEntra.DeleteClientIdentityAsync(record.ProfileId, cancellationToken);
                await RedactClientPresentationAsync(record.ProfileId, cancellationToken);
            }
            else
            {
                await DeactivateAgentApplicationProfileAsync(record.ProfileId, cancellationToken);
            }

            await RemoveMobileProfileSettingsAsync(record, cancellationToken);

            if (!OwnsLease(record, leaseId))
                return new AccountClosureExecutionResult(false, false);

            var closedUtc = DateTime.UtcNow;
            record.State = AccountLifecycleStates.Closed;
            record.ClosedUtc ??= closedUtc;
            record.ClosureLeaseId = null;
            record.ClosureLeaseExpiresUtc = null;
            record.LastClosureErrorCode = null;
            record.UpdatedUtc = closedUtc;
            AddAudit(record, "closure_completed", "closed", closedUtc);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Account closure completed for lifecycle record {AccountLifecycleRecordId} on attempt {AttemptNumber}.",
                record.Id,
                record.ClosureAttemptCount);
            return new AccountClosureExecutionResult(true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Account closure attempt failed for lifecycle record {AccountLifecycleRecordId}.",
                record.Id);
            return await DeferAsync(record, leaseId, "account_closure_retry", cancellationToken);
        }
    }

    private async Task<bool> CancelClientSubscriptionsAsync(
        AccountLifecycleRecord record,
        CancellationToken cancellationToken)
    {
        var subscriptions = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.ClientProfileId == record.ProfileId &&
                                   subscription.Status != ClientSubscriptionStatus.Canceled)
            .Select(subscription => subscription.Id)
            .ToArrayAsync(cancellationToken);

        foreach (var subscriptionId in subscriptions)
        {
            var result = await _billing.CancelClientSubscriptionAsync(
                new CancelClientSubscriptionCommand(
                    subscriptionId,
                    CancelAtPeriodEnd: false,
                    BillingActorType.System,
                    record.UserId,
                    $"account-closure:{record.Id:N}"),
                cancellationToken);
            if (!result.Success)
                return false;
        }

        return true;
    }

    private async Task<string[]> GetIdentityFormsAsync(
        AccountLifecycleRecord record,
        CancellationToken cancellationToken)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize(record.UserId)
        };

        if (record.ParticipantType == MessagingParticipantTypes.Client)
        {
            var profile = await _db.ClientProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == record.ProfileId, cancellationToken);
            if (profile is null)
                throw new InvalidOperationException("The client profile required for account closure no longer exists.");

            identities.Add(Normalize(profile.ClientUserId));
            if (!string.IsNullOrWhiteSpace(profile.ExternalIdentityObjectId))
                identities.Add(Normalize(profile.ExternalIdentityObjectId));
        }

        identities.Remove(string.Empty);
        return identities.ToArray();
    }

    private async Task DeactivatePushDevicesAsync(
        AccountLifecycleRecord record,
        IReadOnlyCollection<string> identityForms,
        CancellationToken cancellationToken)
    {
        var devices = await _db.MobilePushDevices
            .Where(device => identityForms.Contains(device.UserId.ToLower()) &&
                             device.ParticipantType == record.ParticipantType &&
                             device.IsActive)
            .ToArrayAsync(cancellationToken);
        if (devices.Length == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var device in devices)
        {
            device.IsActive = false;
            device.InvalidatedUtc = now;
            device.UpdatedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RedactClientPresentationAsync(Guid clientProfileId, CancellationToken cancellationToken)
    {
        var profile = await _db.ClientProfiles.SingleOrDefaultAsync(
            item => item.Id == clientProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException("The client profile required for account closure no longer exists.");

        var closedEmail = $"closed-{profile.Id:N}@deleted.invalid";
        profile.FirstName = "Closed";
        profile.LastName = "Account";
        profile.Email = closedEmail;
        profile.NormalizedEmail = closedEmail;
        profile.Phone = string.Empty;
        profile.ProfileImageContent = null;
        profile.ProfileImageContentType = null;
        profile.IsVerified = false;
        profile.DOB = null;
        profile.MaritalStatus = string.Empty;
        profile.SignificantOtherFirstName = null;
        profile.SignificantOtherLastName = null;
        profile.SignificantOtherDOB = null;
        profile.SignificantOtherEmail = null;
        profile.SignificantOtherPhone = null;
        profile.AgentNotes = string.Empty;
        profile.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeactivateAgentApplicationProfileAsync(Guid agentProfileId, CancellationToken cancellationToken)
    {
        var profile = await _db.AgentProfiles.SingleOrDefaultAsync(
            item => item.Id == agentProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException("The agent profile required for account closure no longer exists.");

        profile.IsActive = false;
        profile.DeactivatedUtc ??= DateTime.UtcNow;
        profile.DeactivationReason = "Legend application account closure";
        profile.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveMobileProfileSettingsAsync(
        AccountLifecycleRecord record,
        CancellationToken cancellationToken)
    {
        var settings = await _db.MobileProfileSettings
            .Where(item => item.ProfileId == record.ProfileId &&
                           item.ParticipantType == record.ParticipantType)
            .ToArrayAsync(cancellationToken);
        if (settings.Length == 0)
            return;

        _db.MobileProfileSettings.RemoveRange(settings);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<AccountClosureExecutionResult> DeferAsync(
        AccountLifecycleRecord record,
        Guid leaseId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (!OwnsLease(record, leaseId))
            return new AccountClosureExecutionResult(false, false);

        var now = DateTime.UtcNow;
        record.LastClosureErrorCode = errorCode;
        record.ClosureLeaseExpiresUtc = now.Add(RetryDelay);
        record.UpdatedUtc = now;
        AddAudit(record, "closure_deferred", errorCode, now);
        await _db.SaveChangesAsync(cancellationToken);
        return new AccountClosureExecutionResult(true, false, errorCode);
    }

    private void AddAudit(AccountLifecycleRecord record, string action, string resultCode, DateTime occurredUtc)
    {
        _db.AccountLifecycleAuditEntries.Add(new AccountLifecycleAuditEntry
        {
            AccountLifecycleRecordId = record.Id,
            AttemptNumber = record.ClosureAttemptCount,
            Action = action,
            ResultCode = resultCode,
            OccurredUtc = occurredUtc
        });
    }

    private static bool OwnsLease(AccountLifecycleRecord record, Guid leaseId) =>
        record.State == AccountLifecycleStates.DeletionRequested &&
        record.ClosureLeaseId == leaseId;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
