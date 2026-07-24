namespace Shared.Messaging;

public sealed record MessagingCommandCenterViewModel(
    string CurrentUserId,
    string? ServiceContactName = null,
    string? ServiceContactPhone = null,
    string? ServiceContactEmail = null);
