namespace Shared.Messaging;

public sealed record MessagingCommandCenterViewModel(
    string CurrentUserId,
    string? ServiceContactName = null,
    string? ServiceContactPhone = null,
    string? ServiceContactEmail = null,
    string SearchPlaceholder = "Search authorized contacts...",
    string ComposePrompt = "Choose an authorized contact to begin.");
