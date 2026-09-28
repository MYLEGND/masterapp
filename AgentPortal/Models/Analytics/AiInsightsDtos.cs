using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentPortal.Models.Analytics;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed class AiReviewRequestDto
{
    public string? Metric { get; set; }
    public string? Preset { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? TrafficType { get; set; }
    public string? QualityMode { get; set; }
    public Guid? AgentProfileId { get; set; }
    public bool Team { get; set; }
    public string? TimezoneId { get; set; }
    public int? TimezoneOffsetMinutes { get; set; }
}

public sealed class AiFollowUpRequestDto
{
    public string? Metric { get; set; }
    public string? Preset { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? TrafficType { get; set; }
    public string? QualityMode { get; set; }
    public Guid? AgentProfileId { get; set; }
    public bool Team { get; set; }
    public string? TimezoneId { get; set; }
    public int? TimezoneOffsetMinutes { get; set; }
    /// <summary>The follow-up question from the user. Max 500 chars; no PII; no HTML.</summary>
    public string FollowUpQuestion { get; set; } = "";
    /// <summary>The summary text from the prior AI response, included for context.</summary>
    public string? PriorSummary { get; set; }
}

// ── Result DTOs ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakpointSeverity
{
    Low,
    Medium,
    High,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BreakpointOwner
{
    Ad,
    LandingPage,
    Form,
    Tracking,
    FollowUp,
    Unknown
}

public sealed class BreakpointDto
{
    public string Title { get; set; } = "";
    public BreakpointSeverity Severity { get; set; } = BreakpointSeverity.Low;
    public List<string> Evidence { get; set; } = new();
    public string LikelyCause { get; set; } = "";
    public BreakpointOwner Owner { get; set; } = BreakpointOwner.Unknown;
}

public sealed class RecommendedActionDto
{
    public int Priority { get; set; }
    public string Action { get; set; } = "";
    public string Why { get; set; } = "";
    public string ExpectedImpact { get; set; } = "";
}

public sealed class TestToRunDto
{
    public string Name { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    public string Metric { get; set; } = "";
}

public sealed class AiInsightsResultDto
{
    public string Summary { get; set; } = "";
    public int? GrowthOperatorScore { get; set; }
    public string? ScaleReadinessVerdict { get; set; }
    public string? DataTrustWarning { get; set; }
    public List<string> DoNotScaleBecause { get; set; } = new();
    public List<string> NextThreeActions { get; set; } = new();
    public List<BreakpointDto> PrimaryBreakpoints { get; set; } = new();
    public List<RecommendedActionDto> RecommendedActions { get; set; } = new();
    public List<TestToRunDto> TestsToRun { get; set; } = new();
    public List<string> ConfidenceNotes { get; set; } = new();
    /// <summary>True when the result represents an error/failure state rather than real analysis.</summary>
    public bool IsError { get; set; }
    /// <summary>Human-readable error message when IsError is true.</summary>
    public string? ErrorMessage { get; set; }
}

// ── Redacted payload sent to OpenAI (NO PII) ─────────────────────────────────
