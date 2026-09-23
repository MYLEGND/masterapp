namespace Infrastructure.Identity;

/// <summary>Canonical comparison for account login-address changes, independent of UI formatting.</summary>
public static class AccountEmailChange
{
    public static bool IsChanged(string? previousEmail, string? currentEmail) =>
        !string.Equals(previousEmail?.Trim() ?? string.Empty,
            currentEmail?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
