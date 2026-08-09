using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Identity;

/// <summary>
/// Founder-only account closure orchestration. This service deliberately uses
/// the established lifecycle and closure authorities so billing, identity,
/// social content, device registrations, and retention-safe profile handling
/// stay in one audited flow instead of introducing a second delete path.
/// </summary>
public interface IFounderAccountRemovalService
{
    Task<IReadOnlyList<FounderManagedAccount>> ListAsync(
        string? search,
        int take,
        CancellationToken cancellationToken = default);

    Task<FounderAccountRemovalResult> RemoveAsync(
        FounderAccountRemovalCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record FounderManagedAccount(
    Guid ProfileId,
    string UserId,
    string ParticipantType,
    string DisplayName,
    string? Email,
    string LifecycleState,
    bool HasCancelableSubscription,
    bool IsActive);

public sealed record FounderAccountRemovalCommand(
    Guid ProfileId,
    string ParticipantType,
    string FounderUserId,
    string? CorrelationId = null);

public sealed record FounderAccountRemovalResult(
    bool Succeeded,
    bool Completed,
    string? ErrorCode,
    string Message,
    string LifecycleState)
{
    public static FounderAccountRemovalResult Failure(
        string errorCode,
        string message,
        string lifecycleState = AccountLifecycleStates.Active) =>
        new(false, false, errorCode, message, lifecycleState);
}

public sealed class FounderAccountRemovalService : IFounderAccountRemovalService
{
    private const int MaximumTake = 100;

    private readonly MasterAppDbContext _db;
    private readonly IAccountLifecycleService _lifecycle;
    private readonly IAccountClosureService _closure;
    private readonly ILogger<FounderAccountRemovalService> _logger;

    public FounderAccountRemovalService(
        MasterAppDbContext db,
        IAccountLifecycleService lifecycle,
        IAccountClosureService closure,
        ILogger<FounderAccountRemovalService> logger)
    {
        _db = db;
        _lifecycle = lifecycle;
        _closure = closure;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FounderManagedAccount>> ListAsync(
        string? search,
        int take,
        CancellationToken cancellationToken = default)
    {
        var normalizedSearch = NormalizeSearch(search);
        var limit = Math.Clamp(take, 1, MaximumTake);

        // This deliberately does not use the public member directory. Founder
        // account administration must be able to find every account-bearing
        // ClientProfile and AgentProfile, including inactive, unsubscribed, and
        // CRM lead records, while public discovery remains subscription scoped.
        IQueryable<ClientProfile> clientQuery = _db.ClientProfiles
            .AsNoTracking()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ClientUserId));
        if (normalizedSearch is not null)
        {
            clientQuery = clientQuery.Where(profile =>
                (profile.FirstName ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.LastName ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.Email ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.ClientUserId ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.ExternalIdentityObjectId ?? string.Empty).ToLower().Contains(normalizedSearch));
        }

        var clients = await clientQuery
            .OrderBy(profile => profile.FirstName)
            .ThenBy(profile => profile.LastName)
            .ThenBy(profile => profile.Id)
            .Select(profile => new AccountRow(
                profile.Id,
                profile.ClientUserId,
                profile.ExternalIdentityObjectId,
                MessagingParticipantTypes.Client,
                profile.FirstName,
                profile.LastName,
                profile.Email,
                profile.CrmStatus,
                profile.UpdatedUtc,
                _db.ClientSubscriptions.Any(subscription =>
                    subscription.ClientProfileId == profile.Id &&
                    subscription.Status != ClientSubscriptionStatus.Canceled)))
            .Take(limit)
            .ToArrayAsync(cancellationToken);

        IQueryable<AgentProfile> agentQuery = _db.AgentProfiles
            .AsNoTracking()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.AgentUserId));
        if (normalizedSearch is not null)
        {
            agentQuery = agentQuery.Where(profile =>
                (profile.FullName ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.NormalizedEmail ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.AgentUpn ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                (profile.AgentUserId ?? string.Empty).ToLower().Contains(normalizedSearch));
        }

        var agents = await agentQuery
            .OrderBy(profile => profile.FullName)
            .ThenBy(profile => profile.Id)
            .Select(profile => new AccountRow(
                profile.Id,
                profile.AgentUserId,
                null,
                MessagingParticipantTypes.Agent,
                profile.FullName,
                null,
                profile.NormalizedEmail ?? profile.AgentUpn,
                profile.IsActive ? AccountLifecycleStates.Active : "Inactive",
                profile.UpdatedUtc,
                false))
            .Take(limit)
            .ToArrayAsync(cancellationToken);

        var rows = clients
            .Concat(agents)
            .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ParticipantType, StringComparer.Ordinal)
            .ThenBy(row => row.ProfileId)
            .Take(limit)
            .ToArray();

        if (rows.Length == 0)
            return Array.Empty<FounderManagedAccount>();

        var profileIds = rows.Select(row => row.ProfileId).ToArray();
        var lifecycleRows = await _db.AccountLifecycleRecords
            .AsNoTracking()
            .Where(record => profileIds.Contains(record.ProfileId))
            .Select(record => new { record.ProfileId, record.ParticipantType, record.State })
            .ToArrayAsync(cancellationToken);
        var lifecycleByProfile = lifecycleRows.ToDictionary(
            row => new LifecycleProfileKey(row.ProfileId, row.ParticipantType),
            row => row.State);

        return rows.Select(row => new FounderManagedAccount(
                row.ProfileId,
                Normalize(row.ExternalIdentityObjectId) is { Length: > 0 } externalIdentityObjectId
                    ? externalIdentityObjectId
                    : Normalize(row.UserId),
                row.ParticipantType,
                DisplayName(row),
                EmptyToNull(row.Email),
                lifecycleByProfile.GetValueOrDefault(
                    new LifecycleProfileKey(row.ProfileId, row.ParticipantType),
                    row.ParticipantType == MessagingParticipantTypes.Agent &&
                    !string.Equals(row.Status, AccountLifecycleStates.Active, StringComparison.OrdinalIgnoreCase)
                        ? AccountLifecycleStates.Closed
                        : AccountLifecycleStates.Active),
                row.HasCancelableSubscription,
                row.ParticipantType == MessagingParticipantTypes.Agent
                    ? string.Equals(row.Status, AccountLifecycleStates.Active, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(row.Status, "Deleted", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<FounderAccountRemovalResult> RemoveAsync(
        FounderAccountRemovalCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ProfileId == Guid.Empty)
            return FounderAccountRemovalResult.Failure(
                "founder_account_removal_target_required",
                "Choose the Legend account to remove.");

        var participantType = command.ParticipantType?.Trim();
        if (participantType is not (MessagingParticipantTypes.Client or MessagingParticipantTypes.Agent))
        {
            return FounderAccountRemovalResult.Failure(
                "founder_account_removal_target_invalid",
                "The selected account type is not valid.");
        }

        var target = await FindSubjectAsync(command.ProfileId, participantType, cancellationToken);
        if (target is null)
        {
            return FounderAccountRemovalResult.Failure(
                "founder_account_removal_not_found",
                "This Legend account no longer exists.");
        }

        if (MatchesFounderIdentity(target, command.FounderUserId))
        {
            return FounderAccountRemovalResult.Failure(
                "founder_account_removal_self_forbidden",
                "The Founder account cannot be removed from Founder management.");
        }

        var current = await _lifecycle.GetAsync(target.Subject, cancellationToken);
        if (current.State == AccountLifecycleStates.Closed)
        {
            return new FounderAccountRemovalResult(
                true,
                true,
                null,
                "This account was already removed from Legend.",
                current.State);
        }

        var requested = await _lifecycle.RequestDeletionAsync(
            target.Subject,
            command.CorrelationId,
            cancellationToken);
        if (!requested.Succeeded)
        {
            return FounderAccountRemovalResult.Failure(
                requested.ErrorCode ?? "founder_account_removal_unavailable",
                requested.Message ?? "Legend could not start this account removal.",
                requested.Snapshot.State);
        }

        var lifecycleRecord = await _db.AccountLifecycleRecords
            .AsNoTracking()
            .Where(record => record.ProfileId == target.Subject.ProfileId &&
                             record.ParticipantType == target.Subject.ParticipantType)
            .Select(record => new { record.Id, record.ClosureAttemptCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (lifecycleRecord is null)
        {
            return FounderAccountRemovalResult.Failure(
                "founder_account_removal_lifecycle_missing",
                "Legend could not start the protected account-removal workflow.",
                requested.Snapshot.State);
        }

        _db.AccountLifecycleAuditEntries.Add(new AccountLifecycleAuditEntry
        {
            AccountLifecycleRecordId = lifecycleRecord.Id,
            AttemptNumber = lifecycleRecord.ClosureAttemptCount,
            Action = "founder_removal_requested",
            ResultCode = "authorized",
            OccurredUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        var execution = await _closure.ProcessAsync(lifecycleRecord.Id, cancellationToken);
        if (execution.Closed)
        {
            _logger.LogInformation(
                "Founder completed immediate account removal. FounderUserId={FounderUserId} TargetProfileId={TargetProfileId} TargetParticipantType={TargetParticipantType} CorrelationId={CorrelationId}",
                Normalize(command.FounderUserId),
                target.Subject.ProfileId,
                target.Subject.ParticipantType,
                command.CorrelationId);
            return new FounderAccountRemovalResult(
                true,
                true,
                null,
                "Subscription access was cancelled and this account was removed from Legend.",
                AccountLifecycleStates.Closed);
        }

        var pending = await _lifecycle.GetAsync(target.Subject, cancellationToken);
        var errorCode = execution.DeferredCode ?? "founder_account_removal_incomplete";
        _logger.LogWarning(
            "Founder account removal remains incomplete. FounderUserId={FounderUserId} TargetProfileId={TargetProfileId} TargetParticipantType={TargetParticipantType} ErrorCode={ErrorCode} CorrelationId={CorrelationId}",
            Normalize(command.FounderUserId),
            target.Subject.ProfileId,
            target.Subject.ParticipantType,
            errorCode,
            command.CorrelationId);
        return FounderAccountRemovalResult.Failure(
            errorCode,
            "Legend did not complete the protected removal. The account remains closed to access and the lifecycle worker will retry the unfinished step.",
            pending.State);
    }

    private async Task<ResolvedAccountSubject?> FindSubjectAsync(
        Guid profileId,
        string participantType,
        CancellationToken cancellationToken)
    {
        if (participantType == MessagingParticipantTypes.Client)
        {
            var profile = await _db.ClientProfiles
                .AsNoTracking()
                .Where(item => item.Id == profileId && !string.IsNullOrWhiteSpace(item.ClientUserId))
                .Select(item => new { item.ClientUserId, item.ExternalIdentityObjectId })
                .SingleOrDefaultAsync(cancellationToken);
            if (profile is null)
                return null;

            return new ResolvedAccountSubject(
                new AccountLifecycleSubject(
                    Normalize(profile.ExternalIdentityObjectId) is { Length: > 0 } externalIdentityObjectId
                        ? externalIdentityObjectId
                        : Normalize(profile.ClientUserId),
                    participantType,
                    profileId),
                Normalize(profile.ClientUserId),
                Normalize(profile.ExternalIdentityObjectId));
        }

        var agent = await _db.AgentProfiles
            .AsNoTracking()
            .Where(item => item.Id == profileId && !string.IsNullOrWhiteSpace(item.AgentUserId))
            .Select(item => item.AgentUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(agent))
            return null;

        var normalizedAgent = Normalize(agent);
        return new ResolvedAccountSubject(
            new AccountLifecycleSubject(normalizedAgent, participantType, profileId),
            normalizedAgent,
            null);
    }

    private static bool MatchesFounderIdentity(ResolvedAccountSubject target, string founderUserId)
    {
        var normalizedFounder = Normalize(founderUserId);
        return !string.IsNullOrWhiteSpace(normalizedFounder) &&
               (string.Equals(target.Subject.UserId, normalizedFounder, StringComparison.Ordinal) ||
                string.Equals(target.PrimaryUserId, normalizedFounder, StringComparison.Ordinal) ||
                string.Equals(target.ExternalIdentityObjectId, normalizedFounder, StringComparison.Ordinal));
    }

    private static string DisplayName(AccountRow row)
    {
        var value = string.Join(" ", new[] { row.FirstName, row.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? row.Email ?? "Legend account"
            : value;
    }

    private static string? NormalizeSearch(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized[..Math.Min(normalized.Length, 120)];
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AccountRow(
        Guid ProfileId,
        string UserId,
        string? ExternalIdentityObjectId,
        string ParticipantType,
        string? FirstName,
        string? LastName,
        string? Email,
        string? Status,
        DateTime UpdatedUtc,
        bool HasCancelableSubscription);

    private sealed record ResolvedAccountSubject(
        AccountLifecycleSubject Subject,
        string PrimaryUserId,
        string? ExternalIdentityObjectId);

    private readonly record struct LifecycleProfileKey(Guid ProfileId, string ParticipantType);
}
