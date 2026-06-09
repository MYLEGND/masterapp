using System;
using Domain.Entities;

namespace Infrastructure.Leads;

public static class WorkstationLeadOrder
{
    public static long Build(DateTime utc, long descendingOffset = 0)
    {
        var normalizedUtc = utc == default
            ? DateTime.UtcNow
            : utc.Kind == DateTimeKind.Utc
                ? utc
                : utc.ToUniversalTime();

        var seed = normalizedUtc.Ticks;
        if (descendingOffset <= 0)
            return seed;

        return Math.Max(0L, seed - descendingOffset);
    }

    public static long ResolveSortValue(WorkstationLeadProfile? lead)
    {
        if (lead == null)
            return 0L;

        if (lead.CrmOrder > 0)
            return lead.CrmOrder;

        var timestamp = lead.UpdatedUtc != default
            ? lead.UpdatedUtc
            : lead.CreatedUtc;

        return Build(timestamp);
    }
}
