namespace Shared.Calling;

// One wire contract for native apps and browser clients of MessagingHub.
public sealed record LegendCallCommand(
    string Action, Guid DeviceId, Guid? CallId = null, Guid? ConversationId = null,
    bool Video = false, string? SignalKind = null, string? SignalData = null, int Epoch = 0,
    string? PushToken = null, string? PushEnvironment = null);

public sealed record LegendCallPolicy(
    string[] StunUrls, int WifiWidth = 1280, int WifiHeight = 720, int WifiFps = 30,
    int CellularWidth = 640, int CellularHeight = 480, int CellularFps = 24,
    int VideoBitrate = 1_000_000, int AudioBitrate = 64_000,
    int RingSeconds = 45, int ConnectSeconds = 20, int RecoveryAttempts = 3);

public sealed record LegendCallSnapshot(
    Guid Id, Guid ConversationId, string CallerUserId, string CallerType,
    string CalleeUserId, string CalleeType, Guid CallerDeviceId, Guid? CalleeDeviceId,
    string CallerName, string CalleeName, bool Video, string Status,
    DateTime CreatedUtc, DateTime ExpiresUtc, int Epoch,
    string[]? CallerUserIds = null, string[]? CalleeUserIds = null);

public sealed record LegendCallEvent(
    LegendCallSnapshot Call, string? SignalKind = null, string? SignalData = null,
    Guid? FromDeviceId = null, Guid? ToDeviceId = null);

public sealed record LegendCallResult(bool Succeeded, string? Error,
    LegendCallSnapshot? Call = null, LegendCallSnapshot[]? ActiveCalls = null, LegendCallPolicy? Policy = null);

public interface ILegendCallingAuthority
{
    Task<LegendCallResult> ExecuteAsync(string userId, string participantType,
        LegendCallCommand command, CancellationToken cancellationToken);
}
