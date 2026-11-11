using System.Text.Json;
using Infrastructure.Analytics;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed record AssistantFundingEvidence
{
    public bool Verified { get; internal init; }
    public string Reason { get; internal init; } = "cost_bound_unverified";
    public Guid ConnectionId { get; internal init; }
    public Guid ControlRevision { get; internal init; }
    internal Guid ExpectedConnectionRevision { get; init; }
    internal IReadOnlyDictionary<Guid, Guid> ExpectedAccountRevisions { get; init; } = new Dictionary<Guid, Guid>();
    public DateTime ExpiresUtc { get; internal init; }
    public string? PackageSha256 { get; internal init; }
    public IReadOnlyDictionary<Guid, string> FundingScopes { get; internal init; } = new Dictionary<Guid, string>();
}

public sealed partial class ReceptionistOperatorService
{
    // Reuses the private, Founder-reviewed package owner. No UI assertion, API key,
    // accounting opening, voice estimate or credit claim can manufacture this proof.
    public Task<AssistantFundingEvidence> ReadAssistantFundingEvidenceAsync(
        BusinessAssistantConnectionStatus snapshot, CancellationToken ct = default)
    {
        var actor = RequireFounder();
        ct.ThrowIfCancellationRequested();
        var config = snapshot.Configuration;
        if (config is null || snapshot.ConnectionId == Guid.Empty)
            return Task.FromResult(new AssistantFundingEvidence());
        var now = clock.GetUtcNow().UtcDateTime;
        var matches = new List<(ReceptionistOperatorPackage Package, string Hash)>();
        var entries = configuration.GetSection(PackageRoot).GetChildren().Take(129).ToArray();
        if (entries.Length > 128) return Task.FromResult(new AssistantFundingEvidence());
        foreach (var entry in entries)
        {
            var hash = entry["Sha256"];
            if (!HashValue(hash)) continue;
            ReceptionistOperatorPackage package;
            try { package = ReadPackage(entry.Key, hash!, actor); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException) { continue; }
            if (package.Operation != "accept_assistant_cost_bounds" || package.MarketingConnectionId != snapshot.ConnectionId ||
                package.ExpectedOwnerKey != snapshot.WorkspaceKey || package.ConfigurationId != snapshot.Revision ||
                package.Provider != config.Provider || package.ProviderProjectKey != config.ProjectId ||
                package.ConfigurationSha256 != ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(config)) ||
                !package.Evidence.Any(x => x.Kind == "assistant_cost_bounds" && x.Sha256 == config.CostCeilingEvidenceSha256)) continue;
            if (!package.ExpectedAccountRevisions.ContainsKey(config.OwnerBudgetAccountId) ||
                !package.ExpectedAccountRevisions.ContainsKey(config.FundingBudgetAccountId) ||
                !package.FundingScopes.ContainsKey(config.FundingBudgetAccountId)) continue;
            matches.Add((package, hash!));
        }
        if (matches.Count != 1 || config.CostCeilingExpiresUtc <= now)
            return Task.FromResult(new AssistantFundingEvidence());
        var accepted = matches[0];
        return Task.FromResult(new AssistantFundingEvidence { Verified = true, Reason = "operator_reviewed_exact_cost_bounds",
            ConnectionId = snapshot.ConnectionId, ControlRevision = snapshot.Revision,
            ExpiresUtc = accepted.Package.ExpiresUtc < config.CostCeilingExpiresUtc ? accepted.Package.ExpiresUtc : config.CostCeilingExpiresUtc,
            ExpectedConnectionRevision = accepted.Package.ExpectedConnectionRevision,
            ExpectedAccountRevisions = accepted.Package.ExpectedAccountRevisions,
            PackageSha256 = accepted.Hash, FundingScopes = accepted.Package.FundingScopes });
    }
}
