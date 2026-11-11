using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Infrastructure.Analytics;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Shared.Analytics;
using Shared.Workspaces;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendBusinessAssistantFoundationTests
{
    [Fact]
    public void HistoricalConfigurationHashDoesNotChangeWhenImageOptionsAreAbsent()
    {
        const string historical = """
        {"Provider":"openai","ProjectId":"proj_synthetic","OrganizationId":null,"Model":"synthetic-model","OwnerBudgetAccountId":"11111111-1111-1111-1111-111111111111","FundingBudgetAccountId":"22222222-2222-2222-2222-222222222222","MonthlyCashAllocation":25,"MonthlyServiceAllocation":25,"MaximumCashPerTurn":1,"MaximumServicePerTurn":1,"MaximumOutputTokens":512,"CostCeilingEvidenceSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","CostCeilingExpiresUtc":"2026-11-11T00:00:00Z","Enabled":false,"MaximumInputCharacters":32000,"ServiceTier":"default"}
        """;
        var config = JsonSerializer.Deserialize<BusinessAssistantConfiguration>(historical)!;
        Assert.Equal(historical, JsonSerializer.Serialize(config));
        var images = config with { ImagesEnabled = true, ImageModel = "gpt-image-2.5-sunburst", ImageQuality = "high" };
        MarketingConnectionStore.ValidateAssistantConfiguration(images);
        Assert.NotEqual(ReceptionistConfigurationCodec.Hash(historical), ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(images)));
        Assert.Throws<ArgumentException>(() => MarketingConnectionStore.ValidateAssistantConfiguration(config with { ImagesEnabled = true }));
        Assert.Throws<ArgumentException>(() => MarketingConnectionStore.ValidateAssistantConfiguration(config with { DictationEnabled = true }));
        var dictation = config with { DictationEnabled = true, TranscriptionModel = "gpt-transcribe" };
        MarketingConnectionStore.ValidateAssistantConfiguration(dictation);
        Assert.NotEqual(ReceptionistConfigurationCodec.Hash(historical), ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(dictation)));
        Assert.Throws<ArgumentException>(() => MarketingConnectionStore.ValidateAssistantConfiguration(dictation with { Provider = "cloudflare" }));
    }

    [Fact]
    public void WorkspaceParsingRejectsAggregateAliasesAndNoncanonicalIdentities()
    {
        var id = Guid.NewGuid();
        Assert.True(WorkspaceKey.TryParse($"business:{id:N}", out var parsed));
        Assert.Equal(MarketingOwnerScope.Business(id).Workspace, parsed);
        foreach (var invalid in new[] { "global", "client:" + id.ToString("N"), "business:" + id.ToString("D"),
            "business:" + Guid.Empty.ToString("N"), " business:" + id.ToString("N"), $"business:{id:N}".ToUpperInvariant() })
            Assert.False(WorkspaceKey.TryParse(invalid, out _));
    }

    [Fact]
    public void AssistantCredentialProtectionCannotCrossOwnerOrExistingProviderPurpose()
    {
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var other = MarketingOwnerScope.Business(Guid.NewGuid());
        const string synthetic = "synthetic-not-a-provider-key";
        var protectedValue = protector.Protect(owner, "assistant-api", synthetic);
        Assert.Equal(synthetic, protector.Unprotect(owner, "assistant-api", protectedValue));
        Assert.DoesNotContain(synthetic, protectedValue!);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(other, "assistant-api", protectedValue));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(owner, "openai", protectedValue));
    }

    [Fact]
    public void ConfigurationRequiresBoundedInputOutputAndSeparateFundingDimensions()
    {
        var config = Configuration();
        MarketingConnectionStore.ValidateAssistantConfiguration(config);
        foreach (var invalid in new[] { config with { MaximumInputCharacters = 32001 }, config with { MaximumOutputTokens = 4097 },
            config with { FundingBudgetAccountId = config.OwnerBudgetAccountId }, config with { MaximumCashPerTurn = 26m },
            config with { ProjectId = "project\r\nInjected: header" }, config with { CostCeilingEvidenceSha256 = null! } })
            Assert.Throws<ArgumentException>(() => MarketingConnectionStore.ValidateAssistantConfiguration(invalid));
        Assert.False(config.Enabled);
    }

    [Fact]
    public void AssistantEvidenceCannotCarryAnUnrelatedVoiceOrAccountingAction()
    {
        var now = DateTime.UtcNow;
        var package = Package(now);
        package.Validate("synthetic-founder", now);
        foreach (var mixed in new[] { package with { BudgetAccountId = Guid.NewGuid() }, package with { ExpectedCallRevision = Guid.NewGuid() },
            package with { BookingActionId = Guid.NewGuid() }, package with { FinalVoiceSeconds = 0 },
            package with { ExpectedProviderSessionId = "session" }, package with { Enabled = true },
            package with { ComponentKey = "voice" }, package with { RetirementReason = "unrelated" } })
            Assert.Throws<ArgumentException>(() => mixed.Validate("synthetic-founder", now));
        Assert.Throws<ArgumentException>(() => (package with { ExpiresUtc = now }).Validate("synthetic-founder", now));
        Assert.Throws<ArgumentException>(() => package.Validate("different-founder", now));
        Assert.Throws<ArgumentException>(() => (package with { Evidence = package.Evidence.Take(2).ToArray() }).Validate("synthetic-founder", now));
    }

    private static BusinessAssistantConfiguration Configuration() => new("openai", "proj_synthetic", null, "synthetic-model",
        Guid.NewGuid(), Guid.NewGuid(), 25m, 25m, 1m, 1m, 512, new string('a', 64), DateTime.UtcNow.AddHours(1));

    private static ReceptionistOperatorPackage Package(DateTime now)
    {
        var owner = Guid.NewGuid(); var funding = Guid.NewGuid();
        return new() { Version = 1, Label = "synthetic assistant proof", Operation = "accept_assistant_cost_bounds",
            ReviewedByFounderOid = "synthetic-founder", ReviewedUtc = now.AddMinutes(-1), ExpiresUtc = now.AddMinutes(5),
            MarketingConnectionId = Guid.NewGuid(), ConfigurationId = Guid.NewGuid(), ExpectedConnectionRevision = Guid.NewGuid(),
            ConfigurationSha256 = new string('a', 64), Provider = "openai", ProviderProjectKey = "proj_synthetic",
            ExpectedOwnerKey = "business:" + Guid.NewGuid().ToString("N"),
            ExpectedAccountRevisions = new() { [owner] = Guid.NewGuid(), [funding] = Guid.NewGuid() },
            FundingScopes = new() { [funding] = "synthetic-funding" },
            Evidence = new[] { "assistant_cost_bounds", "assistant_project_binding", "assistant_data_processing" }
                .Select(kind => new ReceptionistOperatorEvidence(kind, "synthetic-private-evidence", new string('a', 64), now.AddMinutes(-2))).ToArray() };
    }
}
