using Domain.Entities;
using Infrastructure.Data;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>
/// Single persistence boundary for Meta signal events.
/// All production signal producers must originate from UnifiedEventContext -> UnifiedEventMapper.ToMetaSignal,
/// then persist through this writer. The writer enforces catalog membership and mutually exclusive tenant ownership.
/// </summary>
public static class UnifiedMetaSignalWriter
{
    public static MetaSignalEvent Create(
        UnifiedEventContext context,
        Action<MetaSignalEvent>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!MetaSignalEventCatalog.TryGet(context.EventName, out _))
            throw new InvalidOperationException($"Unknown Meta signal event '{context.EventName}'.");

        var row = UnifiedEventMapper.ToMetaSignal(context);
        configure?.Invoke(row);
        Validate(row);
        return row;
    }

    public static void Write(MasterAppDbContext db, MetaSignalEvent row)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(row);
        Validate(row);
        db.MetaSignalEvents.Add(row);
    }

    private static void Validate(MetaSignalEvent row)
    {
        if (!MetaSignalEventCatalog.TryGet(row.EventName, out _))
            throw new InvalidOperationException($"Unknown Meta signal event '{row.EventName}'.");

        if (row.CommerceBusinessId.HasValue && row.AgentTrackingProfileId.HasValue)
            throw new InvalidOperationException("Meta signal cannot be owned by both a business and an agent.");

        if (row.CommerceBusinessId == Guid.Empty || row.AgentTrackingProfileId == Guid.Empty)
            throw new InvalidOperationException("Meta signal owner identifiers cannot be empty GUIDs.");

        if (string.IsNullOrWhiteSpace(row.EventId))
            throw new InvalidOperationException("Meta signal requires a stable event ID.");
    }
}
