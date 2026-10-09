using System.Text.Json.Serialization;

namespace Shared.Analytics;

public static class OpenAiMeasurementEventNames
{
    public const string PageViewed = "page_viewed";
    public const string LeadCreated = "lead_created";
    public const string AppointmentScheduled = "appointment_scheduled";
    public const string ItemsAdded = "items_added";
    public const string CheckoutStarted = "checkout_started";
    public const string OrderCreated = "order_created";
}

public sealed record OpenAiConversionContent(
    string? Id = null,
    string? Name = null,
    string? ContentType = null,
    int? Quantity = null,
    long? Amount = null,
    string? Currency = null);

public sealed record OpenAiConversionData(
    string Type,
    long? Amount = null,
    string? Currency = null,
    IReadOnlyList<OpenAiConversionContent>? Contents = null);

public sealed record OpenAiConversionUser(
    string? Obref = null,
    IReadOnlyList<string>? EmailsSha256 = null,
    IReadOnlyList<string>? PhoneNumbersSha256 = null,
    IReadOnlyList<string>? ExternalIdsSha256 = null,
    IReadOnlyList<string>? FirstNamesSha256 = null,
    IReadOnlyList<string>? LastNamesSha256 = null,
    IReadOnlyList<string>? Regions = null,
    IReadOnlyList<string>? PostalCodes = null,
    IReadOnlyList<string>? Cities = null,
    IReadOnlyList<string>? Countries = null,
    string? IpAddress = null,
    string? UserAgent = null);

public sealed record OpenAiConversionEvent(
    string Id,
    string Type,
    long TimestampMs,
    string SourceUrl,
    string ActionSource,
    OpenAiConversionData Data,
    string? Oppref = null,
    string? CustomEventName = null,
    OpenAiConversionUser? User = null);

public sealed record OpenAiConversionsApiResult(
    bool Attempted,
    bool Sent,
    bool Retryable,
    int? HttpStatusCode,
    string Status,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ProviderReceiptJson = null);

public sealed record OpenAiMeasurementHealthSnapshot(
    MarketingOwnerScope Owner,
    bool Connected,
    bool PixelConfigured,
    bool ConversionsApiConfigured,
    string? PixelId,
    int PendingDeliveries,
    int RetryableDeliveries,
    int FailedDeliveries,
    int SentDeliveries,
    DateTime? LastSentUtc,
    bool ProviderMonitoringAvailable,
    int RecentProviderEvents,
    string Status,
    int OtherDestinationReceipts = 0,
    int OtherDestinationUnresolved = 0);
