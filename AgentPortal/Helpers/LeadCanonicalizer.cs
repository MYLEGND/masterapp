using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Helpers;

/// <summary>
/// Provides a single, deterministic canonical selector for duplicate WorkstationLeadProfile rows.
/// Rule order:
/// 1) Most recently UpdatedUtc
/// 2) Most recently CreatedUtc
/// 3) Highest CallCount
/// 4) Lexicographically smallest LeadId (stable tie-breaker)
/// </summary>
public static class LeadCanonicalizer
{
    public static IReadOnlyList<WorkstationLeadProfile> Canonicalize(
        IEnumerable<WorkstationLeadProfile> rows,
        ILogger? logger = null,
        string? context = null)
    {
        var list = rows?.ToList() ?? new List<WorkstationLeadProfile>();
        if (list.Count <= 1) return list;

        var duplicates = list
            .GroupBy(l => l.LeadId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0 && logger != null)
        {
            logger.LogWarning("Lead canonicalization encountered {DuplicateGroupCount} duplicate LeadId group(s) in {Context}",
                duplicates.Count, context ?? "unknown");
        }

        return list
            .GroupBy(l => l.LeadId, StringComparer.OrdinalIgnoreCase)
            .Select(SelectCanonicalInternal)
            .ToList();
    }

    public static WorkstationLeadProfile? SelectCanonical(
        IEnumerable<WorkstationLeadProfile> rows,
        ILogger? logger = null,
        string? context = null)
    {
        var canon = Canonicalize(rows, logger, context);
        return canon.FirstOrDefault();
    }

    private static WorkstationLeadProfile SelectCanonicalInternal(
        IGrouping<string, WorkstationLeadProfile> group)
    {
        return group
            .OrderByDescending(l => l.UpdatedUtc)
            .ThenByDescending(l => l.CreatedUtc)
            .ThenByDescending(l => l.CallCount)
            .ThenBy(l => l.LeadId, StringComparer.Ordinal)
            .First();
    }
}
