namespace Domain.Accounts;

/// <summary>
/// The server-owned lifecycle state for one typed Legend identity. A user ID is
/// not sufficient because the same Entra identity can legitimately hold both
/// agent and client participant roles.
/// </summary>
public static class AccountLifecycleStates
{
    public const string Active = "Active";
    public const string Paused = "Paused";
    public const string DeletionRequested = "DeletionRequested";
    public const string Closed = "Closed";
}

public sealed record AccountLifecycleSubject(
    string UserId,
    string ParticipantType,
    Guid ProfileId);

public sealed record AccountLifecycleSnapshot(
    string State,
    bool AllowsFullAccess,
    bool CanResume,
    DateTime? PausedUtc,
    DateTime? DeletionRequestedUtc,
    DateTime? ClosedUtc);

public sealed record AccountLifecycleOperationResult(
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    AccountLifecycleSnapshot Snapshot)
{
    public static AccountLifecycleOperationResult Failure(
        string errorCode,
        string message,
        AccountLifecycleSnapshot snapshot) =>
        new(false, errorCode, message, snapshot);

    public static AccountLifecycleOperationResult Success(
        string? message,
        AccountLifecycleSnapshot snapshot) =>
        new(true, null, message, snapshot);
}
