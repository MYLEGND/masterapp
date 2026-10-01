using System.Security.Cryptography;
using System.Text;
using Domain.Engineering;
using Domain.Entities;
using Shared.Diagnostics;

namespace AgentPortal.Services.Engineering;

internal static class LegendEngineeringPolicies
{
    internal const string ApprovedIntegrationBranch = "legend/approved-changes";

    internal static bool IsApprovedAutonomousBaseBranch(string? value) =>
        string.Equals(value?.Trim(), ApprovedIntegrationBranch, StringComparison.Ordinal);

    internal static EngineeringPolicyDecision Classify(RuntimeDiagnosticIncident incident)
    {
        var disclosure = FounderSoftwareRemediationService.ClassifyInspectableSourcePath(incident.SourceFilePath ?? string.Empty);
        var failureClass = ClassifyFailure(incident, disclosure);
        var canonicalAuthority = CanonicalAuthority(incident, disclosure);
        var projects = Projects(incident.SourceFilePath, disclosure, incident.AppIdentifier);
        var applications = string.IsNullOrWhiteSpace(incident.AppIdentifier)
            ? Array.Empty<string>()
            : [SafeToken(incident.AppIdentifier, 64)];
        var impact = ImpactSet(canonicalAuthority, incident.SourceFilePath, disclosure, applications);
        var severity = Severity(incident);
        var revenue = RevenueImpact(incident);
        var users = UserImpact(incident);
        var frequency = Frequency(incident.Occurrences);
        var confidence = Confidence(incident, disclosure);
        var breadth = impact.Contains("shared:*", StringComparer.Ordinal) ? 100 : Math.Min(100, 20 + projects.Length * 15 + applications.Length * 10);
        var priority = WeightedPriority(severity, revenue, users, frequency, confidence, breadth);
        var priorityClass = PriorityClass(priority, severity, revenue);
        var risk = RiskClass(incident, disclosure);
        var complexity = Complexity(incident, projects.Length, applications.Length, breadth, risk);
        var codeEligible = failureClass == EngineeringFailureClass.CodeDefect && risk != EngineeringRiskClass.TierC;
        var role = codeEligible
            ? complexity >= 70 || risk == EngineeringRiskClass.TierB ? EngineeringRole.HeadGpt : EngineeringRole.CodexImplementer
            : failureClass == EngineeringFailureClass.Unknown
                ? complexity >= 70 ? EngineeringRole.HeadGpt : EngineeringRole.TriageWorker
                : EngineeringRole.Sentinel;
        var modelTier = role == EngineeringRole.TriageWorker
            ? EngineeringModelTier.FastTriage
            : ModelTier(complexity, risk, codeEligible);
        var cohort = ReleaseCohort(priorityClass);

        return new(
            failureClass, severity, revenue, users, frequency, confidence, breadth,
            priority, priorityClass, risk, complexity, role, modelTier,
            canonicalAuthority, projects, applications, impact,
            codeEligible, risk != EngineeringRiskClass.TierA, cohort);
    }

    internal static int WeightedPriority(int severity, int revenueImpact, int affectedUsers, int frequency, int confidence, int dependencyBreadth)
    {
        static int N(int value) => Math.Clamp(value, 0, 100);
        var weighted =
            N(severity) * 35L +
            N(revenueImpact) * 25L +
            N(affectedUsers) * 15L +
            N(frequency) * 10L +
            N(confidence) * 10L +
            N(dependencyBreadth) * 5L;
        return (int)Math.Round(weighted / 100.0, MidpointRounding.AwayFromZero);
    }

    internal static string PriorityClass(int score, int severity, int revenueImpact)
    {
        if (severity >= 90 || revenueImpact >= 95 || score >= 85) return "P1";
        if (score >= 65) return "P2";
        if (score >= 35) return "P3";
        return "P4";
    }

    internal static string ReleaseCohort(string priorityClass) => priorityClass switch
    {
        "P1" => "IMMEDIATE",
        "P2" => "FOUR_HOUR_MAX",
        "P3" => "DAILY",
        _ => "WEEKLY"
    };

    internal static bool ImpactSetsOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Contains("shared:*", StringComparer.Ordinal) || right.Contains("shared:*", StringComparer.Ordinal))
            return true;
        var set = new HashSet<string>(left, StringComparer.Ordinal);
        return right.Any(set.Contains);
    }

    internal static string ComputeWorkKey(string canonicalAuthorityKey, string liveSha)
        => Sha256(canonicalAuthorityKey + "|" + liveSha.ToLowerInvariant());

    internal static string EvidenceRevision(RuntimeDiagnosticIncident incident, IReadOnlyList<Guid>? incidentIds = null)
    {
        var ids = incidentIds is null ? incident.Id.ToString("N") :
            string.Join(",", incidentIds.OrderBy(id => id).Select(id => id.ToString("N")));
        return Sha256(string.Join("|",
            ids,
            incident.DeduplicationKey,
            incident.ReviewVersion,
            incident.Occurrences,
            incident.LastSeenUtc.ToUniversalTime().Ticks,
            incident.ReleaseVerified,
            incident.GitCommitHash ?? "unknown"));
    }

    internal static bool IsImmutableSha(string? value)
        => value?.Length == 40 && value.All(Uri.IsHexDigit) && value.Any(ch => ch != '0');

    private static string ClassifyFailure(RuntimeDiagnosticIncident incident, string? disclosure)
    {
        var category = (incident.Category ?? string.Empty).ToUpperInvariant();
        var error = (incident.ErrorName ?? string.Empty).ToUpperInvariant();

        if (category.Contains("EXPECTED_POLICY", StringComparison.Ordinal) || error.Contains("EXPECTED_POLICY", StringComparison.Ordinal))
            return EngineeringFailureClass.ExpectedPolicyBehavior;
        if (category.Contains("CONFIG", StringComparison.Ordinal) || error.Contains("CONFIG", StringComparison.Ordinal) ||
            error.Contains("MISSING_SETTING", StringComparison.Ordinal))
            return EngineeringFailureClass.ConfigurationDefect;
        if (category.Contains("PROVIDER", StringComparison.Ordinal) || category.Contains("NETWORK", StringComparison.Ordinal) ||
            error.Contains("TIMEOUT", StringComparison.Ordinal) || error.Contains("DNS", StringComparison.Ordinal) ||
            error.Contains("PROVIDER", StringComparison.Ordinal))
            return EngineeringFailureClass.NetworkProviderFailure;
        if (incident.StatusCode is 401 or 403 || category.Contains("AUTHORIZATION", StringComparison.Ordinal) ||
            error.Contains("FORBIDDEN", StringComparison.Ordinal) || error.Contains("UNAUTHORIZED", StringComparison.Ordinal))
            return EngineeringFailureClass.AuthorizationDenial;
        if (category.Contains("DEPLOYMENT_DRIFT", StringComparison.Ordinal) ||
            error.Contains("DEPLOYMENT_DRIFT", StringComparison.Ordinal))
            return EngineeringFailureClass.DeploymentDrift;
        if (disclosure == LegendSiteToolDisclosureAuthority.SafeSource &&
            (incident.StatusCode >= 500 || category.Contains("CODE", StringComparison.Ordinal) ||
             category.Contains("RUNTIME", StringComparison.Ordinal) || error.Contains("EXCEPTION", StringComparison.Ordinal)))
            return EngineeringFailureClass.CodeDefect;
        return EngineeringFailureClass.Unknown;
    }

    private static string CanonicalAuthority(RuntimeDiagnosticIncident incident, string? disclosure)
    {
        var source = NormalizePath(incident.SourceFilePath);
        if (!string.IsNullOrWhiteSpace(source) && disclosure == LegendSiteToolDisclosureAuthority.SafeSource)
            return source.Contains('/', StringComparison.Ordinal)
                ? "source:" + source
                : "source:" + SafeToken(incident.AppIdentifier, 64) + ":" + source;
        if (!string.IsNullOrWhiteSpace(source))
            return "protected:" + Sha256(source)[..20];
        return "route:" + SafeToken(incident.AppIdentifier, 64) + ":" + SafeRoute(incident.Route);
    }

    private static string[] Projects(string? sourcePath, string? disclosure, string? application)
    {
        if (disclosure != LegendSiteToolDisclosureAuthority.SafeSource) return [];
        var path = NormalizePath(sourcePath);
        if (string.IsNullOrWhiteSpace(path)) return [];
        var slash = path.IndexOf('/');
        if (slash >= 0) return [path[..slash]];
        var app = SafeToken(application, 64);
        return app == "unknown" ? [] : [app];
    }

    private static string[] ImpactSet(string canonicalAuthority, string? sourcePath, string? disclosure, IReadOnlyList<string> applications)
    {
        var values = new HashSet<string>(StringComparer.Ordinal) { "authority:" + canonicalAuthority };
        var path = NormalizePath(sourcePath);
        if (disclosure != LegendSiteToolDisclosureAuthority.SafeSource ||
            path.StartsWith("SHARED/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Domain/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Legend-Design/", StringComparison.OrdinalIgnoreCase))
        {
            values.Add("shared:*");
        }
        else
        {
            foreach (var app in applications) values.Add("app:" + app);
        }
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static int Severity(RuntimeDiagnosticIncident incident)
    {
        var status = incident.StatusCode ?? 0;
        var baseScore = status >= 500 ? 85 : status is 401 or 403 ? 75 : status >= 400 ? 55 : 45;
        if (incident.Recurred) baseScore += 8;
        if (incident.Occurrences >= 25) baseScore += 7;
        return Math.Clamp(baseScore, 0, 100);
    }

    private static int RevenueImpact(RuntimeDiagnosticIncident incident)
    {
        var route = (incident.Route ?? string.Empty).ToLowerInvariant();
        var name = (incident.ErrorName ?? string.Empty).ToLowerInvariant();
        var text = route + "|" + name;
        if (ContainsAny(text, "login", "signin", "lead", "checkout", "payment", "publish", "booking", "appointment", "conversion", "analytics", "ad"))
            return 95;
        if (ContainsAny(text, "crm", "website", "editor", "modal", "client"))
            return 65;
        return 25;
    }

    private static int UserImpact(RuntimeDiagnosticIncident incident)
    {
        if (incident.Occurrences >= 100) return 95;
        if (incident.Occurrences >= 25) return 80;
        if (incident.Occurrences >= 5) return 60;
        return 35;
    }

    private static int Frequency(long occurrences)
        => occurrences >= 100 ? 100 : occurrences >= 25 ? 80 : occurrences >= 10 ? 65 : occurrences >= 3 ? 45 : 25;

    private static int Confidence(RuntimeDiagnosticIncident incident, string? disclosure)
    {
        if (incident.ReleaseVerified && disclosure == LegendSiteToolDisclosureAuthority.SafeSource && IsImmutableSha(incident.GitCommitHash)) return 95;
        if (disclosure == LegendSiteToolDisclosureAuthority.SafeSource && IsImmutableSha(incident.GitCommitHash)) return 80;
        if (IsImmutableSha(incident.GitCommitHash)) return 65;
        return 45;
    }

    private static string RiskClass(RuntimeDiagnosticIncident incident, string? disclosure)
    {
        if (disclosure is null || disclosure is LegendSiteToolDisclosureAuthority.PrivacyProtected or LegendSiteToolDisclosureAuthority.IntegrityProtected or LegendSiteToolDisclosureAuthority.ExistenceOnly)
            return EngineeringRiskClass.TierC;
        var path = (incident.SourceFilePath ?? string.Empty).ToLowerInvariant();
        var route = (incident.Route ?? string.Empty).ToLowerInvariant();
        var text = path + "|" + route;
        if (ContainsAny(text, "auth", "identity", "security", "billing", "payment", "migration", "retention", "ownership", "advertis", "spend", "database", "dbcontext", "infrastructure"))
            return EngineeringRiskClass.TierB;
        return EngineeringRiskClass.TierA;
    }

    private static int Complexity(RuntimeDiagnosticIncident incident, int projectCount, int appCount, int breadth, string risk)
    {
        var path = (incident.SourceFilePath ?? string.Empty).ToLowerInvariant();
        var score = 15 + Math.Min(20, projectCount * 8) + Math.Min(20, appCount * 8) + breadth / 5;
        if (ContainsAny(path, "controller", "service", "infrastructure", "domain")) score += 12;
        if (ContainsAny(path, "dbcontext", "migration", "auth", "identity", "billing", "payment", "security")) score += 22;
        if (risk == EngineeringRiskClass.TierB) score += 15;
        if (risk == EngineeringRiskClass.TierC) score += 30;
        if (incident.Recurred) score += 8;
        return Math.Clamp(score, 0, 100);
    }

    private static string ModelTier(int complexity, string risk, bool codeEligible)
    {
        if (!codeEligible && complexity < 45) return EngineeringModelTier.FastTriage;
        if (risk == EngineeringRiskClass.TierC || complexity >= 80) return EngineeringModelTier.DeepReasoning;
        if (codeEligible) return EngineeringModelTier.CodeImplementation;
        return complexity >= 55 ? EngineeringModelTier.DeepReasoning : EngineeringModelTier.StandardReasoning;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));

    private static string NormalizePath(string? value)
        => (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

    private static string SafeToken(string? value, int max)
    {
        var text = (value ?? "unknown").Trim();
        if (text.Length > max) text = text[..max];
        return new string(text.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
    }

    private static string SafeRoute(string? value)
    {
        var route = (value ?? "/unknown").Trim();
        if (!route.StartsWith('/')) route = "/" + route;
        return route.Length <= 160 ? route : route[..160];
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
