using System;
using System.Collections.Generic;

namespace AgentPortal.Models;

public class AgencyCommandDashboardVm
{
    public IReadOnlyList<AgentCommandCardVm> Agents { get; set; } = new List<AgentCommandCardVm>();
    public CommandKpiVm Overall { get; set; } = new();
    public AgencyRevenueVm Revenue { get; set; } = new();
}

public class CommandKpiVm
{
    public int TotalAgents { get; set; }
    public int TotalLeads { get; set; }
    public int TotalClients { get; set; }
    public int CallsToday { get; set; }
    public int CallsWeek { get; set; }
    public int FollowUpsDue { get; set; }
}

public class AgentCommandCardVm
{
    public string AgentUserId { get; set; } = ""; // canonical OID
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Title { get; set; }
    public int? DisplayOrder { get; set; }

    public int LeadCount { get; set; }
    public int ClientCount { get; set; }
    public int CallsToday { get; set; }
    public int CallsWeek { get; set; }
    public int FollowUpsDue { get; set; }

    public Dictionary<string, int> PipelineByStage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class AgentCommandDetailVm
{
    public AgentCommandCardVm Agent { get; set; } = new();
    public List<LeadSummaryVm> Leads { get; set; } = new();
    public List<ClientSummaryVm> Clients { get; set; } = new();
    public bool ViewAsEnabled { get; set; }
    public string? EffectiveAgentName { get; set; }
    public string? EffectiveAgentEmail { get; set; }
    public CommandKpiVm Kpi { get; set; } = new();
    public List<PipelineSliceVm> Pipeline { get; set; } = new();
    public List<FollowUpItemVm> FollowUps { get; set; } = new();
}

public class LeadSummaryVm
{
    public string LeadId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "";
    public int CallsToday { get; set; }
    public int CallsWeek { get; set; }
}

public class ClientSummaryVm
{
    public string ClientUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Priority { get; set; }
    public string? Status { get; set; }
    public DateTime? NextTouch { get; set; }
    public string? NextNote { get; set; }
}

public class PipelineSliceVm
{
    public string Stage { get; set; } = "";
    public int Count { get; set; }
}

public class FollowUpItemVm
{
    public string ClientUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public string? Note { get; set; }
}
