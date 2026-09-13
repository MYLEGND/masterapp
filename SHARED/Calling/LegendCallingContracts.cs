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
    int RingSeconds = 45, int ConnectSeconds = 20, int RecoveryAttempts = 3, LegendCallAdaptationPolicy? Adaptation = null, LegendCallRelay? Relay = null, LegendCallScreenSharePolicy? ScreenShare = null);

public sealed record LegendCallRelay(string[] Urls, string Username, string Credential, DateTime ExpiresUtc)
{
    public static bool IsConfigurationValid(string[] urls, string? secret) =>
        urls.Length > 0 && !string.IsNullOrWhiteSpace(secret) && secret.Length >= 32 && urls.All(IsEndpointValid);

    private static bool IsEndpointValid(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 0 || value[..colon].ToLowerInvariant() is not ("turn" or "turns") || value.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate("https://" + value[(colon + 1)..], UriKind.Absolute, out var uri)) return false;
        return uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Fragment.Length == 0 &&
            uri.Query is "" or "?transport=udp" or "?transport=tcp";
    }
}

public sealed record LegendCallAdaptationPolicy(int SampleSeconds = 3, int RecoverySamples = 4,
    int LowBandwidth = 350_000, int HighBandwidth = 900_000, double HighLatencySeconds = 0.6,
    int LowWidth = 320, int LowHeight = 240, int LowFps = 12, int LowBitrate = 180_000,
    int MediumWidth = 640, int MediumHeight = 480, int MediumFps = 18, int MediumBitrate = 450_000, double AudioPriority = 4);

// Screen content preserves text resolution by reducing frame rate before
// falling back to a lower resolution. Audio retains the shared priority.
public sealed record LegendCallScreenSharePolicy(
    int HighWidth = 1920, int HighHeight = 1080, int HighFps = 15, int HighBitrate = 2_500_000,
    int MediumWidth = 1280, int MediumHeight = 720, int MediumFps = 10, int MediumBitrate = 1_200_000,
    int LowWidth = 960, int LowHeight = 540, int LowFps = 5, int LowBitrate = 250_000,
    double TransportHeadroomFraction = 0.15);

public sealed record LegendCallSnapshot(
    Guid Id, Guid ConversationId, string CallerUserId, string CallerType,
    string CalleeUserId, string CalleeType, Guid CallerDeviceId, Guid? CalleeDeviceId,
    string CallerName, string CalleeName, bool Video, string Status,
    DateTime CreatedUtc, DateTime ExpiresUtc, int Epoch,
    string[]? CallerUserIds = null, string[]? CalleeUserIds = null,
    DateTime? ReceivedUtc = null, string? CallerImagePath = null)
{
    // Every client uses the same authoritative delivery and terminal messages.
    public string? FailureMessage => Status switch
    {
        "missed" when ReceivedUtc == null => "The recipient could not be reached. Their device did not confirm receiving the call.",
        "missed" => "The call was not answered.",
        "declined" => "The call was declined.",
        _ => null
    };
}

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
