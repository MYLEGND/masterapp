namespace Infrastructure.Mobile;

/// <summary>
/// Read-only mobile representation of the authoritative Financial Health
/// Snapshot. It transports the calculated balance-sheet composition without
/// creating another finance store or calculation path for the native app.
/// </summary>
public sealed record MobileFinancialHealthSnapshot(
    DateTime UpdatedUtc,
    IReadOnlyList<MobileFinancialHealthSection> Sections);

/// <summary>
/// A meaningful Financial Health Snapshot section. The section key and the
/// server-provided rows are stable native presentation data, not editable
/// financial state.
/// </summary>
public sealed record MobileFinancialHealthSection(
    string Key,
    string Title,
    string Semantic,
    string? Period,
    IReadOnlyList<MobileFinancialHealthGroup> Groups,
    MobileFinancialHealthMetric? Total);

/// <summary>
/// Retains the authoritative grouping within a financial section, including
/// the individual protection categories and their primary/spouse dimensions.
/// </summary>
public sealed record MobileFinancialHealthGroup(
    string Key,
    string? Title,
    IReadOnlyList<MobileFinancialHealthMetric> Metrics);

/// <summary>
/// One server-authoritative balance-sheet fact. Monetary values are integer
/// cents; the native app renders them directly and never derives totals.
/// </summary>
public sealed record MobileFinancialHealthMetric(
    string Key,
    string Label,
    string ValueType,
    long? AmountCents,
    decimal? NumericValue,
    string? TextValue,
    string? Status);
