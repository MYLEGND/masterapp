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
        FounderAccountDirectoryScope scope = FounderAccountDirectoryScope.Active,
        CancellationToken cancellationToken = default);

    Task<FounderAccountRemovalResult> RemoveAsync(
        FounderAccountRemovalCommand command,
        CancellationToken cancellationToken = default);

    Task<FounderAccountRemovalBatchResult> RemoveManyAsync(
        FounderAccountRemovalBatchCommand command,
        CancellationToken cancellationToken = default);

    Task<FounderAccountPurgeResult> PurgeArchivedAsync(
        FounderAccountPurgeCommand command,
        CancellationToken cancellationToken = default);

    Task<FounderAccountPurgeBatchResult> PurgeArchivedManyAsync(
        FounderAccountRemovalBatchCommand command,
        CancellationToken cancellationToken = default);
}

public enum FounderAccountDirectoryScope
{
    Active,
    Archive
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

public sealed record FounderAccountTarget(Guid ProfileId, string ParticipantType);

public sealed record FounderAccountRemovalBatchCommand(
    IReadOnlyCollection<FounderAccountTarget> Accounts,
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

public sealed record FounderAccountRemovalBatchResult(
    IReadOnlyList<FounderAccountRemovalResult> Results)
{
    public int CompletedCount => Results.Count(result => result.Succeeded && result.Completed);
    public int FailedCount => Results.Count(result => !result.Succeeded);
}

public sealed record FounderAccountPurgeCommand(
    Guid ProfileId,
    string ParticipantType,
    string FounderUserId,
    string? CorrelationId = null);

public sealed record FounderAccountPurgeResult(
    bool Succeeded,
    string? ErrorCode,
    string Message)
{
    public static FounderAccountPurgeResult Failure(string errorCode, string message) =>
        new(false, errorCode, message);
}

public sealed record FounderAccountPurgeBatchResult(
    IReadOnlyList<FounderAccountPurgeResult> Results)
{
    public int CompletedCount => Results.Count(result => result.Succeeded);
    public int FailedCount => Results.Count(result => !result.Succeeded);
}

public sealed class FounderAccountRemovalService : IFounderAccountRemovalService
{
    private const int MaximumTake = 100;
    private const int MaximumBatchSize = 25;

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
        FounderAccountDirectoryScope scope = FounderAccountDirectoryScope.Active,
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
        clientQuery = scope == FounderAccountDirectoryScope.Archive
            ? clientQuery.Where(profile => _db.AccountLifecycleRecords.Any(record =>
                record.ProfileId == profile.Id &&
                record.ParticipantType == MessagingParticipantTypes.Client &&
                record.State == AccountLifecycleStates.Closed))
            : clientQuery.Where(profile => !_db.AccountLifecycleRecords.Any(record =>
                record.ProfileId == profile.Id &&
                record.ParticipantType == MessagingParticipantTypes.Client &&
                record.State == AccountLifecycleStates.Closed));

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
        agentQuery = scope == FounderAccountDirectoryScope.Archive
            ? agentQuery.Where(profile => _db.AccountLifecycleRecords.Any(record =>
                record.ProfileId == profile.Id &&
                record.ParticipantType == MessagingParticipantTypes.Agent &&
                record.State == AccountLifecycleStates.Closed))
            : agentQuery.Where(profile => !_db.AccountLifecycleRecords.Any(record =>
                record.ProfileId == profile.Id &&
                record.ParticipantType == MessagingParticipantTypes.Agent &&
                record.State == AccountLifecycleStates.Closed));

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

        var accounts = rows.Select(row => new FounderManagedAccount(
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
                        ? "Inactive"
                        : AccountLifecycleStates.Active),
                row.HasCancelableSubscription,
                row.ParticipantType == MessagingParticipantTypes.Agent
                    ? string.Equals(row.Status, AccountLifecycleStates.Active, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(row.Status, "Deleted", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return scope == FounderAccountDirectoryScope.Archive
            ? accounts.Where(account => IsClosed(account.LifecycleState)).ToArray()
            : accounts.Where(account => !IsClosed(account.LifecycleState)).ToArray();
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
                "This account is already in the Founder Archive.",
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
                "Subscription access was cancelled and this account is now in the Founder Archive.",
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

    public async Task<FounderAccountRemovalBatchResult> RemoveManyAsync(
        FounderAccountRemovalBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        var targets = NormalizeTargets(command.Accounts);
        if (targets.Count == 0)
        {
            return new FounderAccountRemovalBatchResult(new[]
            {
                FounderAccountRemovalResult.Failure(
                    "founder_account_removal_target_required",
                    "Choose at least one Legend account to remove.")
            });
        }

        if (targets.Count > MaximumBatchSize)
        {
            return new FounderAccountRemovalBatchResult(new[]
            {
                FounderAccountRemovalResult.Failure(
                    "founder_account_removal_batch_limit",
                    $"Choose no more than {MaximumBatchSize} accounts at once.")
            });
        }

        var results = new List<FounderAccountRemovalResult>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RemoveAsync(
                new FounderAccountRemovalCommand(
                    target.ProfileId,
                    target.ParticipantType,
                    command.FounderUserId,
                    command.CorrelationId),
                cancellationToken));
        }

        return new FounderAccountRemovalBatchResult(results);
    }

    /// <summary>
    /// Irreversibly erases a profile only after the canonical closure workflow
    /// has completed. Payment/audit rows are intentionally detached by their
    /// existing database relations instead of being destroyed; they contain no
    /// remaining profile reference after the account root is removed.
    /// </summary>
    public async Task<FounderAccountPurgeResult> PurgeArchivedAsync(
        FounderAccountPurgeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ProfileId == Guid.Empty)
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_target_required",
                "Choose the archived Legend account to erase.");

        var participantType = command.ParticipantType?.Trim();
        if (participantType is not (MessagingParticipantTypes.Client or MessagingParticipantTypes.Agent))
        {
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_target_invalid",
                "The selected account type is not valid.");
        }

        var target = await FindSubjectAsync(command.ProfileId, participantType, cancellationToken);
        if (target is null)
        {
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_not_found",
                "This archived Legend account no longer exists.");
        }

        if (MatchesFounderIdentity(target, command.FounderUserId))
        {
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_self_forbidden",
                "The Founder account cannot be erased from Founder management.");
        }

        var lifecycle = await _lifecycle.GetAsync(target.Subject, cancellationToken);
        if (!IsClosed(lifecycle.State))
        {
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_not_archived",
                "Only accounts whose protected removal is complete can be erased from the Archive.");
        }

        var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (participantType == MessagingParticipantTypes.Client)
                await PurgeClientProfileAsync(target, cancellationToken);
            else
                await PurgeAgentProfileAsync(target, cancellationToken);

            await PurgeSharedAccountRowsAsync(target, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Founder permanently erased an archived account. FounderUserId={FounderUserId} TargetProfileId={TargetProfileId} TargetParticipantType={TargetParticipantType} CorrelationId={CorrelationId}",
                Normalize(command.FounderUserId),
                target.Subject.ProfileId,
                target.Subject.ParticipantType,
                command.CorrelationId);
            return new FounderAccountPurgeResult(
                true,
                null,
                "The archived Legend account was permanently erased from the application database.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);

            _logger.LogError(
                exception,
                "Founder archived-account purge failed. FounderUserId={FounderUserId} TargetProfileId={TargetProfileId} TargetParticipantType={TargetParticipantType} CorrelationId={CorrelationId}",
                Normalize(command.FounderUserId),
                target.Subject.ProfileId,
                target.Subject.ParticipantType,
                command.CorrelationId);
            return FounderAccountPurgeResult.Failure(
                "founder_account_purge_unavailable",
                "Legend could not erase this archived account. No partial removal was committed.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<FounderAccountPurgeBatchResult> PurgeArchivedManyAsync(
        FounderAccountRemovalBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        var targets = NormalizeTargets(command.Accounts);
        if (targets.Count == 0)
        {
            return new FounderAccountPurgeBatchResult(new[]
            {
                FounderAccountPurgeResult.Failure(
                    "founder_account_purge_target_required",
                    "Choose at least one archived Legend account to erase.")
            });
        }

        if (targets.Count > MaximumBatchSize)
        {
            return new FounderAccountPurgeBatchResult(new[]
            {
                FounderAccountPurgeResult.Failure(
                    "founder_account_purge_batch_limit",
                    $"Choose no more than {MaximumBatchSize} accounts at once.")
            });
        }

        var results = new List<FounderAccountPurgeResult>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await PurgeArchivedAsync(
                new FounderAccountPurgeCommand(
                    target.ProfileId,
                    target.ParticipantType,
                    command.FounderUserId,
                    command.CorrelationId),
                cancellationToken));
        }

        return new FounderAccountPurgeBatchResult(results);
    }

    private async Task PurgeClientProfileAsync(
        ResolvedAccountSubject target,
        CancellationToken cancellationToken)
    {
        var profile = await _db.ClientProfiles.SingleOrDefaultAsync(
            item => item.Id == target.Subject.ProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException("The archived client profile no longer exists.");

        var ownedHouseholdIds = await _db.HouseholdAccounts
            .Where(item => item.SubscriptionOwnerClientProfileId == profile.Id)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (ownedHouseholdIds.Length > 0)
        {
            var hasOtherHouseholdMembers = await _db.HouseholdMemberships.AnyAsync(
                item => ownedHouseholdIds.Contains(item.HouseholdAccountId) &&
                        item.ClientProfileId != null &&
                        item.ClientProfileId != profile.Id &&
                        item.Status != Domain.Enums.HouseholdMembershipStatus.Removed,
                cancellationToken);
            if (hasOtherHouseholdMembers)
            {
                throw new InvalidOperationException(
                    "An archived household owner cannot be erased while another household member remains active.");
            }
        }

        var memberships = await _db.HouseholdMemberships
            .Where(item => item.ClientProfileId == profile.Id || ownedHouseholdIds.Contains(item.HouseholdAccountId))
            .ToArrayAsync(cancellationToken);
        var membershipIds = memberships.Select(item => item.Id).ToArray();
        var householdIds = memberships
            .Where(item => ownedHouseholdIds.Contains(item.HouseholdAccountId))
            .Select(item => item.HouseholdAccountId)
            .Distinct()
            .ToArray();

        var findingIds = await _db.FinancialFindings
            .Where(item => item.ClientProfileId == profile.Id)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (findingIds.Length > 0)
        {
            _db.FinancialFindingObservations.RemoveRange(await _db.FinancialFindingObservations
                .Where(item => findingIds.Contains(item.FinancialFindingId))
                .ToArrayAsync(cancellationToken));
        }

        _db.FinancialFindingFeedback.RemoveRange(await _db.FinancialFindingFeedback
            .Where(item => item.ClientProfileId == profile.Id || findingIds.Contains(item.FinancialFindingId))
            .ToArrayAsync(cancellationToken));
        _db.FinancialFindings.RemoveRange(await _db.FinancialFindings
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.FinancialObservations.RemoveRange(await _db.FinancialObservations
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ClientFinancialIntelligenceProfiles.RemoveRange(await _db.ClientFinancialIntelligenceProfiles
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ExpenseLensStreamLinks.RemoveRange(await _db.ExpenseLensStreamLinks
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.RecurringFinancialStreams.RemoveRange(await _db.RecurringFinancialStreams
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ImportedFinancialTransactions.RemoveRange(await _db.ImportedFinancialTransactions
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ImportedFinancialAccounts.RemoveRange(await _db.ImportedFinancialAccounts
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.FinancialDataConnections.RemoveRange(await _db.FinancialDataConnections
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));

        _db.ClientFinancialPlans.RemoveRange(await _db.ClientFinancialPlans
            .Where(item => item.ClientId == profile.Id)
            .ToArrayAsync(cancellationToken));
        if (householdIds.Length > 0)
        {
            _db.FinanceToolStates.RemoveRange(await _db.FinanceToolStates
                .Where(item => item.HouseholdAccountId != null && householdIds.Contains(item.HouseholdAccountId.Value))
                .ToArrayAsync(cancellationToken));
        }
        if (membershipIds.Length > 0)
        {
            _db.HouseholdMemberInvitations.RemoveRange(await _db.HouseholdMemberInvitations
                .Where(item => membershipIds.Contains(item.HouseholdMembershipId))
                .ToArrayAsync(cancellationToken));
        }
        if (householdIds.Length > 0)
        {
            _db.HouseholdMemberInvitations.RemoveRange(await _db.HouseholdMemberInvitations
                .Where(item => householdIds.Contains(item.HouseholdAccountId))
                .ToArrayAsync(cancellationToken));
        }
        _db.HouseholdMemberships.RemoveRange(memberships);
        if (ownedHouseholdIds.Length > 0)
        {
            _db.HouseholdAccounts.RemoveRange(await _db.HouseholdAccounts
                .Where(item => ownedHouseholdIds.Contains(item.Id))
                .ToArrayAsync(cancellationToken));
        }

        _db.ClientBillingNotifications.RemoveRange(await _db.ClientBillingNotifications
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ClientIdentityContinuations.RemoveRange(await _db.ClientIdentityContinuations
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        var subscriptions = await _db.ClientSubscriptions
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var subscription in subscriptions)
            subscription.DefaultPaymentMethodId = null;
        _db.ClientSubscriptions.RemoveRange(subscriptions);
        _db.ClientPaymentMethods.RemoveRange(await _db.ClientPaymentMethods
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ClientSubscriptionOffers.RemoveRange(await _db.ClientSubscriptionOffers
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.ClientEntitlements.RemoveRange(await _db.ClientEntitlements
            .Where(item => item.ClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));

        _db.JourneyCircleConnections.RemoveRange(await _db.JourneyCircleConnections
            .Where(item => item.RequesterClientProfileId == profile.Id || item.RecipientClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.JourneyCircleBlocks.RemoveRange(await _db.JourneyCircleBlocks
            .Where(item => item.BlockerClientProfileId == profile.Id || item.BlockedClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));
        _db.JourneyCircleReports.RemoveRange(await _db.JourneyCircleReports
            .Where(item => item.ReporterClientProfileId == profile.Id || item.ReportedClientProfileId == profile.Id)
            .ToArrayAsync(cancellationToken));

        _db.ClientProfiles.Remove(profile);
    }

    private async Task PurgeAgentProfileAsync(
        ResolvedAccountSubject target,
        CancellationToken cancellationToken)
    {
        var profile = await _db.AgentProfiles.SingleOrDefaultAsync(
            item => item.Id == target.Subject.ProfileId,
            cancellationToken)
            ?? throw new InvalidOperationException("The archived agent profile no longer exists.");
        var userId = Normalize(profile.AgentUserId);

        _db.AgentFinanceToolStates.RemoveRange(await _db.AgentFinanceToolStates
            .Where(item => item.AgentUserId.ToLower() == userId)
            .ToArrayAsync(cancellationToken));
        _db.AgentAssistants.RemoveRange(await _db.AgentAssistants
            .Where(item => item.ParentAgentUserId.ToLower() == userId || item.AssistantUserId!.ToLower() == userId)
            .ToArrayAsync(cancellationToken));
        _db.AgentZoomLinks.RemoveRange(await _db.AgentZoomLinks
            .Where(item => item.AgentUserId.ToLower() == userId)
            .ToArrayAsync(cancellationToken));
        _db.GraphCalendarSubscriptions.RemoveRange(await _db.GraphCalendarSubscriptions
            .Where(item => item.AgentUserId.ToLower() == userId)
            .ToArrayAsync(cancellationToken));
        _db.AgentProfiles.Remove(profile);
    }

    private async Task PurgeSharedAccountRowsAsync(
        ResolvedAccountSubject target,
        CancellationToken cancellationToken)
    {
        var userIds = new[]
            {
                target.Subject.UserId,
                target.PrimaryUserId,
                target.ExternalIdentityObjectId
            }
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var participantType = target.Subject.ParticipantType;

        var deviceIds = await _db.MobilePushDevices
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (deviceIds.Length > 0)
        {
            _db.MobilePushDeliveries.RemoveRange(await _db.MobilePushDeliveries
                .Where(item => deviceIds.Contains(item.MobilePushDeviceId))
                .ToArrayAsync(cancellationToken));
        }
        _db.MobilePushDevices.RemoveRange(await _db.MobilePushDevices
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.MobileActivityNotifications.RemoveRange(await _db.MobileActivityNotifications
            .Where(item => userIds.Contains(item.RecipientUserId.ToLower()) && item.RecipientParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.UserGlobalBadges.RemoveRange(await _db.UserGlobalBadges
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.MobileProfileSettings.RemoveRange(await _db.MobileProfileSettings
            .Where(item => item.ProfileId == target.Subject.ProfileId && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.ControlledResourceGrants.RemoveRange(await _db.ControlledResourceGrants
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.LegendTranslationUsageLedgers.RemoveRange(await _db.LegendTranslationUsageLedgers
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.LegendTranslationUsagePeriods.RemoveRange(await _db.LegendTranslationUsagePeriods
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.LegendTranslationEntitlements.RemoveRange(await _db.LegendTranslationEntitlements
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.VerificationReviewRequests.RemoveRange(await _db.VerificationReviewRequests
            .Where(item => userIds.Contains(item.RequesterUserId.ToLower()) && item.RequesterParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.MessageConversationParticipants.RemoveRange(await _db.MessageConversationParticipants
            .Where(item => userIds.Contains(item.UserId.ToLower()) && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
        _db.MessagingAuditEntries.RemoveRange(await _db.MessagingAuditEntries
            .Where(item => userIds.Contains(item.ActorUserId.ToLower()) ||
                           (item.TargetUserId != null && userIds.Contains(item.TargetUserId.ToLower())))
            .ToArrayAsync(cancellationToken));

        if (participantType == MessagingParticipantTypes.Client)
        {
            _db.ClientAgentMessagingGrants.RemoveRange(await _db.ClientAgentMessagingGrants
                .Where(item => userIds.Contains(item.ClientUserId.ToLower()))
                .ToArrayAsync(cancellationToken));
        }
        else
        {
            _db.ClientAgentMessagingGrants.RemoveRange(await _db.ClientAgentMessagingGrants
                .Where(item => userIds.Contains(item.AgentUserId.ToLower()) ||
                               userIds.Contains(item.GrantedByAgentUserId.ToLower()))
                .ToArrayAsync(cancellationToken));
        }

        _db.AccountLifecycleAuditEntries.RemoveRange(await _db.AccountLifecycleAuditEntries
            .Where(item => _db.AccountLifecycleRecords
                .Where(record => record.ProfileId == target.Subject.ProfileId && record.ParticipantType == participantType)
                .Select(record => record.Id)
                .Contains(item.AccountLifecycleRecordId))
            .ToArrayAsync(cancellationToken));
        _db.AccountLifecycleRecords.RemoveRange(await _db.AccountLifecycleRecords
            .Where(item => item.ProfileId == target.Subject.ProfileId && item.ParticipantType == participantType)
            .ToArrayAsync(cancellationToken));
    }

    private static IReadOnlyList<FounderAccountTarget> NormalizeTargets(
        IReadOnlyCollection<FounderAccountTarget>? targets) =>
        (targets ?? Array.Empty<FounderAccountTarget>())
            .Where(target => target.ProfileId != Guid.Empty)
            .Select(target => target with { ParticipantType = target.ParticipantType?.Trim() ?? string.Empty })
            .DistinctBy(target => new LifecycleProfileKey(target.ProfileId, target.ParticipantType))
            .ToArray();

    private static bool IsClosed(string? state) =>
        string.Equals(state, AccountLifecycleStates.Closed, StringComparison.OrdinalIgnoreCase);

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
