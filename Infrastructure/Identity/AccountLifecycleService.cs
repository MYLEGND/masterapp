using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Identity;

/// <summary>
/// The one server authority for member pause and closure-request state. It owns
/// access state only; it delegates subscription transitions to the existing
/// billing orchestrator and leaves irreversible retention work to the approved
/// deletion policy rather than guessing at regulated records.
/// </summary>
public interface IAccountLifecycleService
{
    Task<AccountLifecycleSnapshot> GetAsync(AccountLifecycleSubject subject, CancellationToken cancellationToken = default);
    Task<AccountLifecycleOperationResult> PauseAsync(AccountLifecycleSubject subject, string? correlationId = null, CancellationToken cancellationToken = default);
    Task<AccountLifecycleOperationResult> ResumeAsync(AccountLifecycleSubject subject, string? correlationId = null, CancellationToken cancellationToken = default);
    Task<AccountLifecycleOperationResult> RequestDeletionAsync(AccountLifecycleSubject subject, string? correlationId = null, CancellationToken cancellationToken = default);
}

public sealed class AccountLifecycleService : IAccountLifecycleService
{
    private readonly MasterAppDbContext _db;
    private readonly IBillingOrchestrator _billing;

    public AccountLifecycleService(MasterAppDbContext db, IBillingOrchestrator billing)
    {
        _db = db;
        _billing = billing;
    }

    public async Task<AccountLifecycleSnapshot> GetAsync(AccountLifecycleSubject subject, CancellationToken cancellationToken = default)
    {
        var record = await FindAsync(subject, tracking: false, cancellationToken);
        return ToSnapshot(record);
    }

    public async Task<AccountLifecycleOperationResult> PauseAsync(
        AccountLifecycleSubject subject,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await GetOrCreateAsync(subject, cancellationToken);
        if (record.State == AccountLifecycleStates.DeletionRequested || record.State == AccountLifecycleStates.Closed)
            return AccountLifecycleOperationResult.Failure("ACCOUNT_CLOSURE_IN_PROGRESS", "This account cannot be paused because closure is already in progress.", ToSnapshot(record));

        if (record.State == AccountLifecycleStates.Paused)
            return AccountLifecycleOperationResult.Success("Your account is already paused.", ToSnapshot(record));

        if (IsClient(subject))
        {
            var billing = await _billing.PauseClientSubscriptionAsync(
                new AccountLifecycleSubscriptionCommand(subject.ProfileId, subject.UserId, correlationId),
                cancellationToken);
            if (!billing.Success)
            {
                return AccountLifecycleOperationResult.Failure(
                    billing.SafeErrorCode ?? "ACCOUNT_PAUSE_UNAVAILABLE",
                    billing.SanitizedSummary ?? "Your account could not be paused.",
                    ToSnapshot(record));
            }
        }

        record.State = AccountLifecycleStates.Paused;
        record.PausedUtc = DateTime.UtcNow;
        record.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return AccountLifecycleOperationResult.Success("Your Legend account is paused. Resume it from Account access when you are ready to return.", ToSnapshot(record));
    }

    public async Task<AccountLifecycleOperationResult> ResumeAsync(
        AccountLifecycleSubject subject,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await FindAsync(subject, tracking: true, cancellationToken);
        if (record is null || record.State == AccountLifecycleStates.Active)
            return AccountLifecycleOperationResult.Success("Your Legend account is active.", ToSnapshot(record));

        if (record.State is AccountLifecycleStates.DeletionRequested or AccountLifecycleStates.Closed)
            return AccountLifecycleOperationResult.Failure("ACCOUNT_CLOSURE_IN_PROGRESS", "This account cannot be resumed because closure is in progress.", ToSnapshot(record));

        if (IsClient(subject))
        {
            var billing = await _billing.ResumeClientSubscriptionAsync(
                new AccountLifecycleSubscriptionCommand(subject.ProfileId, subject.UserId, correlationId),
                cancellationToken);
            if (!billing.Success)
            {
                return AccountLifecycleOperationResult.Failure(
                    billing.SafeErrorCode ?? "ACCOUNT_RESUME_UNAVAILABLE",
                    billing.SanitizedSummary ?? "Your account could not be resumed.",
                    ToSnapshot(record));
            }
        }

        record.State = AccountLifecycleStates.Active;
        record.PausedUtc = null;
        record.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return AccountLifecycleOperationResult.Success("Your Legend account is active again.", ToSnapshot(record));
    }

    public async Task<AccountLifecycleOperationResult> RequestDeletionAsync(
        AccountLifecycleSubject subject,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var record = await GetOrCreateAsync(subject, cancellationToken);
        if (record.State == AccountLifecycleStates.Closed)
            return AccountLifecycleOperationResult.Success("This account is already closed.", ToSnapshot(record));

        if (record.State != AccountLifecycleStates.DeletionRequested)
        {
            record.State = AccountLifecycleStates.DeletionRequested;
            record.DeletionRequestedUtc = DateTime.UtcNow;
            record.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // The request immediately terminates Legend application access. Actual
        // record deletion/anonymization remains a server-side policy operation;
        // no retention duration or billing disposition is invented here.
        return AccountLifecycleOperationResult.Success(
            "Your account closure request was recorded and Legend access is now disabled while the required data and billing lifecycle is completed.",
            ToSnapshot(record));
    }

    private async Task<AccountLifecycleRecord?> FindAsync(AccountLifecycleSubject subject, bool tracking, CancellationToken cancellationToken)
    {
        var normalized = Normalize(subject);
        IQueryable<AccountLifecycleRecord> query = _db.AccountLifecycleRecords;
        if (!tracking)
            query = query.AsNoTracking();

        var record = await query.SingleOrDefaultAsync(
            item => item.UserId == normalized.UserId &&
                    item.ParticipantType == normalized.ParticipantType,
            cancellationToken);

        if (record is not null && record.ProfileId != normalized.ProfileId)
        {
            throw new InvalidOperationException(
                "The resolved profile does not match the established account lifecycle subject.");
        }

        return record;
    }

    private async Task<AccountLifecycleRecord> GetOrCreateAsync(AccountLifecycleSubject subject, CancellationToken cancellationToken)
    {
        var normalized = Normalize(subject);
        var existing = await FindAsync(normalized, tracking: true, cancellationToken);
        if (existing is not null)
            return existing;

        var record = new AccountLifecycleRecord
        {
            UserId = normalized.UserId,
            ParticipantType = normalized.ParticipantType,
            ProfileId = normalized.ProfileId,
            State = AccountLifecycleStates.Active
        };
        _db.AccountLifecycleRecords.Add(record);
        return record;
    }

    private static AccountLifecycleSubject Normalize(AccountLifecycleSubject subject)
    {
        if (subject.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(subject.UserId))
            throw new ArgumentException("A resolved account lifecycle subject is required.", nameof(subject));

        var participantType = subject.ParticipantType?.Trim();
        if (participantType is not (MessagingParticipantTypes.Agent or MessagingParticipantTypes.Client))
            throw new ArgumentException("The account lifecycle participant type is invalid.", nameof(subject));

        return subject with
        {
            UserId = subject.UserId.Trim().ToLowerInvariant(),
            ParticipantType = participantType
        };
    }

    private static bool IsClient(AccountLifecycleSubject subject) =>
        string.Equals(subject.ParticipantType, MessagingParticipantTypes.Client, StringComparison.Ordinal);

    private static AccountLifecycleSnapshot ToSnapshot(AccountLifecycleRecord? record)
    {
        var state = record?.State ?? AccountLifecycleStates.Active;
        return new AccountLifecycleSnapshot(
            state,
            state == AccountLifecycleStates.Active,
            state == AccountLifecycleStates.Paused,
            record?.PausedUtc,
            record?.DeletionRequestedUtc,
            record?.ClosedUtc);
    }
}
