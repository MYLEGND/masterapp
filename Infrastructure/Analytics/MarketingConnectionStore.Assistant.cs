using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

// Selection and allocations extend the existing owner/provider connection. Provider
// grants are deliberately absent: a selected API key is not evidence of a credit balance.
public sealed record BusinessAssistantConfiguration(
    string Provider, string ProjectId, string? OrganizationId, string Model,
    Guid OwnerBudgetAccountId, Guid FundingBudgetAccountId,
    decimal MonthlyCashAllocation, decimal MonthlyServiceAllocation,
    decimal MaximumCashPerTurn, decimal MaximumServicePerTurn,
    int MaximumOutputTokens, string CostCeilingEvidenceSha256, DateTime CostCeilingExpiresUtc,
    bool Enabled = false, int MaximumInputCharacters = 32000, string ServiceTier = "default",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageQuality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ImagesEnabled = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TranscriptionModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DictationEnabled = false);

public sealed record BusinessAssistantConnectionStatus(Guid ConnectionId, Guid Revision,
    string WorkspaceKey, BusinessAssistantConfiguration? Configuration, bool CredentialConfigured,
    string Readiness, string CreditBalanceStatus, DateTime ObservedUtc);

public sealed partial class MarketingConnectionStore
{
    private sealed record AssistantCredential(string WorkspaceKey, string Provider,
        string ProjectId, string? OrganizationId, string Secret);
    private static readonly JsonSerializerOptions AssistantJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public async Task<BusinessAssistantConnectionStatus> GetAssistantConnectionAsync(
        MarketingOwnerScope owner, string provider, CancellationToken ct = default)
    {
        ValidateAssistantOwner(owner);
        var row = await GetStatusAsync(owner, provider, ct);
        var configuration = ReadAssistantConfiguration(row?.AssistantConfigurationJson);
        var configured = !string.IsNullOrWhiteSpace(row?.AssistantCredentialCiphertext);
        return new(row?.Id ?? Guid.Empty, row?.AssistantControlRevision ?? Guid.Empty, owner.Key,
            configuration, configured, row?.DisconnectedUtc is not null ? "revoked" :
            configuration is null ? "not_configured" : !configuration.Enabled ? "disabled" :
            configuration.Provider != "openai" ? "business_provider_binding_not_admitted" :
            configuration.Provider == "openai" && !configured ? "credential_missing" : "funding_admission_required",
            "unknown", DateTime.UtcNow);
    }

    public async Task<BusinessAssistantConnectionStatus> SaveAssistantConfigurationAsync(
        MarketingOwnerScope owner, BusinessAssistantConfiguration configuration, Guid expectedRevision,
        string? replacementCredential, Func<MasterAppDbContext, Task> authorize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        ValidateAssistantOwner(owner);
        ValidateAssistantConfiguration(configuration);
        await ReceptionistSql.TransactionAsync(ReceptionistSql.Options(db), [], async write =>
        {
            await authorize(write);
            var row = await write.MarketingConnections.SingleOrDefaultAsync(x =>
                x.OwnerKey == owner.Key && x.Provider == configuration.Provider, ct);
            if (row is null)
            {
                if (expectedRevision != Guid.Empty) throw new InvalidOperationException("Assistant connection changed.");
                row = new MarketingConnection { OwnerKey = owner.Key, OwnerType = owner.OwnerType,
                    CommerceBusinessId = owner.CommerceBusinessId, AgentTrackingProfileId = owner.AgentTrackingProfileId,
                    Provider = configuration.Provider };
                write.Add(row);
            }
            else if (row.AssistantControlRevision != expectedRevision || ReceptionistSql.Owner(row) != owner)
                throw new InvalidOperationException("Assistant connection changed.");
            var prior = ReadAssistantConfiguration(row.AssistantConfigurationJson);
            if (prior?.ProjectId != configuration.ProjectId || prior?.OrganizationId != configuration.OrganizationId)
                row.AssistantCredentialCiphertext = null;
            if (replacementCredential is not null)
            {
                if (configuration.Provider != "openai" || string.IsNullOrWhiteSpace(replacementCredential) || replacementCredential.Length > 8192)
                    throw new ArgumentException("A bounded server-side project API credential is required.");
                row.AssistantCredentialCiphertext = protector.Protect(owner, "assistant-api",
                    JsonSerializer.Serialize(new AssistantCredential(owner.Key, configuration.Provider,
                        configuration.ProjectId, configuration.OrganizationId, replacementCredential), AssistantJson));
            }
            row.AssistantConfigurationJson = JsonSerializer.Serialize(configuration, AssistantJson);
            row.AssistantControlRevision = Guid.NewGuid();
            row.Revision = Guid.NewGuid(); row.UpdatedUtc = DateTime.UtcNow;
            // Do not change Ads/voice readiness, existing credentials or revocation.
            return true;
        }, ct);
        return await GetAssistantConnectionAsync(owner, configuration.Provider, ct);
    }

    public async Task<string?> GetAssistantCredentialAsync(MarketingOwnerScope owner, string provider,
        Guid expectedRevision, CancellationToken ct = default)
    {
        ValidateAssistantOwner(owner);
        var row = await GetStatusAsync(owner, provider, ct);
        var config = ReadAssistantConfiguration(row?.AssistantConfigurationJson);
        if (row is null || row.DisconnectedUtc is not null || row.AssistantControlRevision != expectedRevision ||
            config is not { Enabled: true } || config.Provider != "openai" || row.AssistantCredentialCiphertext is null)
            return null;
        var value = JsonSerializer.Deserialize<AssistantCredential>(
            protector.Unprotect(owner, "assistant-api", row.AssistantCredentialCiphertext)!, AssistantJson);
        if (value is null || value.WorkspaceKey != owner.Key || value.Provider != config.Provider ||
            value.ProjectId != config.ProjectId || value.OrganizationId != config.OrganizationId)
            throw new InvalidOperationException("Assistant credential binding is inconsistent.");
        return value.Secret;
    }

    internal static BusinessAssistantConfiguration? ReadAssistantConfiguration(string? json)
    {
        if (json is null) return null;
        var value = JsonSerializer.Deserialize<BusinessAssistantConfiguration>(json, AssistantJson)
            ?? throw new InvalidOperationException("Assistant configuration is invalid.");
        ValidateAssistantConfiguration(value); return value;
    }

    private static void ValidateAssistantOwner(MarketingOwnerScope owner)
    {
        if (owner.OwnerType is not ("business" or "agent"))
            throw new ArgumentException("Select one business or agent workspace.");
    }

    internal static void ValidateAssistantConfiguration(BusinessAssistantConfiguration value)
    {
        static bool Token(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max &&
            s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
        if (value.ServiceTier != "default" || value.Provider is not ("openai" or "cloudflare" or "legend-native") || !Token(value.ProjectId, 200) ||
            value.ReasoningEffort is not (null or "low" or "medium" or "high") ||
            value.TranscriptionModel is not (null or "gpt-transcribe") ||
            (value.DictationEnabled && (value.Provider != "openai" || value.TranscriptionModel is null)) ||
            (value.ImageModel is not null && (!Token(value.ImageModel, 100) || !value.ImageModel.StartsWith("gpt-image-", StringComparison.Ordinal))) ||
            value.ImageQuality is not (null or "low" or "medium" or "high") || (value.ImagesEnabled && (value.Provider != "openai" || value.ImageModel is null || value.ImageQuality is null)) ||
            !Token(value.Model, 100) || (value.OrganizationId is not null && !Token(value.OrganizationId, 200)) ||
            value.MaximumOutputTokens is < 64 or > 4096 || value.MaximumInputCharacters is < 1000 or > 32000 || value.MonthlyCashAllocation < 0 || value.MonthlyServiceAllocation < 0 ||
            value.MaximumCashPerTurn < 0 || value.MaximumServicePerTurn <= 0 ||
            value.MaximumCashPerTurn > value.MonthlyCashAllocation || value.MaximumServicePerTurn > value.MonthlyServiceAllocation ||
            value.OwnerBudgetAccountId == Guid.Empty || value.FundingBudgetAccountId == Guid.Empty ||
            value.OwnerBudgetAccountId == value.FundingBudgetAccountId || value.CostCeilingEvidenceSha256 is null || value.CostCeilingEvidenceSha256.Length != 64 ||
            value.CostCeilingEvidenceSha256.Any(c => !char.IsAsciiHexDigit(c)) || value.CostCeilingExpiresUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A complete bounded provider, project and funding configuration is required.");
    }
}
