namespace Domain.Enums;

/// <summary>
/// The client-controlled boundary for an agent's access to the client workspace.
/// Values are stored as strings so existing profiles can safely default to the
/// long-standing shared-account behavior during the database migration.
/// </summary>
public static class ClientAccountManagementModes
{
    public const string SharedAccount = "SharedAccount";
    public const string SelfManaged = "SelfManaged";

    public static bool IsValid(string? value) =>
        string.Equals(value?.Trim(), SharedAccount, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), SelfManaged, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), SelfManaged, StringComparison.OrdinalIgnoreCase)
            ? SelfManaged
            : SharedAccount;

    /// <summary>
    /// Missing legacy values are shared by design; unknown non-empty values
    /// fail closed until they are explicitly corrected.
    /// </summary>
    public static bool AllowsAgentWorkspaceAccess(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), SharedAccount, StringComparison.OrdinalIgnoreCase);
}
