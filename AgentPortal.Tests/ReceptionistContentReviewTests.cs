using System;
using System.Linq;
using System.Text.Json.Nodes;
using Infrastructure.Reception;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistContentReviewTests
{
    [Theory]
    [InlineData("budget")]
    [InlineData("privacy")]
    [InlineData("policy")]
    [InlineData("capability")]
    [InlineData("hours")]
    [InlineData("notification")]
    public void ContentReviewCannotChangeProtectedDimensions(string change)
    {
        var original = Configuration();
        var draft = change switch
        {
            "budget" => original with { MonthlyCashLimit = 41m },
            "privacy" => original with { ContactAndRetentionPolicyJson = "{\"policyText\":\"different\"}" },
            "policy" => original with { BusinessPolicy = "Different full policy." },
            "capability" => original with { RequestedCapabilities = [] },
            "hours" => original with { Hours = [] },
            _ => original with { IntakeRulesJson = original.IntakeRulesJson.Replace("false", "true", StringComparison.Ordinal) }
        };
        Assert.ThrowsAny<Exception>(() => ReceptionistContentReview.Validate(MarketingOwnerScope.Founder, original, draft));
    }

    [Fact]
    public void RoutineContentAndExplicitSupportedNewServiceRemainReusable()
    {
        var original = Configuration();
        var services = JsonNode.Parse(original.ServiceDefinitionsJson)!.AsArray();
        var added = services[0]!.DeepClone(); added["serviceKey"] = "new_service"; added["displayName"] = "Synthetic new service";
        added["aliases"] = new JsonArray("synthetic new"); services.Add(added);
        var intake = JsonNode.Parse(original.IntakeRulesJson)!;
        intake["canonicalInquiry"]!["services"]!.AsArray().Add(new JsonObject { ["serviceKey"] = "new_service", ["crmBucket"] = "ServiceInquiry" });
        var draft = original with { BusinessName = "Changed synthetic name", ConversationInstructions = "Reviewed AI greeting.",
            ServiceDefinitionsJson = services.ToJsonString(), IntakeRulesJson = intake.ToJsonString() };
        ReceptionistContentReview.Validate(MarketingOwnerScope.Business(Guid.NewGuid()), original, draft);
        var bad = draft with { IntakeRulesJson = draft.IntakeRulesJson.Replace("ServiceInquiry", "unknown-route", StringComparison.Ordinal) };
        Assert.ThrowsAny<Exception>(() => ReceptionistContentReview.Validate(MarketingOwnerScope.Business(Guid.NewGuid()), original, bad));
    }

    [Fact]
    public void ReviewIdentityBindsOwnerActorRevisionDraftAndBaseline()
    {
        var revision = Guid.NewGuid(); var draft = Guid.NewGuid(); var baseline = Guid.NewGuid();
        string Receipt(string owner = "owner-a", string actor = "actor-a", string hash = "draft-hash", string binding = "binding-hash") =>
            ReceptionistContentReview.Receipt(owner, actor, revision, draft, hash, baseline, binding);
        Assert.Equal(Receipt(), Receipt());
        Assert.NotEqual(Receipt(), Receipt(owner: "owner-b")); Assert.NotEqual(Receipt(), Receipt(actor: "actor-b"));
        Assert.NotEqual(Receipt(), Receipt(hash: "different")); Assert.NotEqual(Receipt(), Receipt(binding: "different"));
        Assert.NotEqual(Receipt(), ReceptionistContentReview.Receipt("owner-a", "actor-a", Guid.NewGuid(), draft, "draft-hash", baseline, "binding-hash"));
    }

    [Fact]
    public void CompactPolicyMustStayBoundToTheFullReviewedPolicy()
    {
        var original = Configuration();
        var compact = original with { CompactVoicePolicy = "Synthetic concise AI guidance.",
            CompactVoicePolicySourceSha256 = ReceptionistConfigurationCodec.Hash(original.BusinessPolicy) };
        ReceptionistContentReview.Validate(MarketingOwnerScope.Founder, original, compact);
        Assert.Throws<ArgumentException>(() => ReceptionistConfigurationCodec.Validate(compact with { BusinessPolicy = "A changed policy." }));
        Assert.Throws<ArgumentException>(() => ReceptionistConfigurationCodec.Validate(compact with { CompactVoicePolicy = null }));
        Assert.Null(original.CompactVoicePolicy);
    }

    [Fact]
    public void LegacyConfigurationKeepsItsOriginalCanonicalFingerprintWhenCompactFieldsAreAbsent()
    {
        var configuration = Configuration();
        var json = ReceptionistConfigurationCodec.Serialize(configuration);
        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove("compactVoicePolicy"); legacy.Remove("compactVoicePolicySourceSha256");
        var originalJson = legacy.ToJsonString();
        var originalHash = ReceptionistConfigurationCodec.Hash(originalJson);
        var loaded = ReceptionistConfigurationCodec.Read(originalJson, originalHash);
        Assert.Equal(originalJson, ReceptionistConfigurationCodec.Serialize(loaded));
        Assert.Equal(originalHash, ReceptionistConfigurationCodec.Hash(ReceptionistConfigurationCodec.Serialize(loaded)));
    }

    [Fact]
    public void PartialContentProvenanceCannotBeReadAsAcceptedAuthority()
    {
        var binding = new ReceptionistProviderBinding { ContentReviewHash = new string('a', 64) };
        Assert.True(ReceptionistConfigurationCodec.HasContentProvenance(binding));
        Assert.Throws<ArgumentException>(() => ReceptionistConfigurationCodec.ValidateBinding(binding, Configuration()));
    }
    private static ReceptionistBusinessConfiguration Configuration() => new()
    {
        BusinessName = "Synthetic owner", ConversationInstructions = "Approved greeting.", BusinessPolicy = "Approved caller policy.",
        TimeZoneId = "UTC", Currency = "USD", MonthlyCashLimit = 40m, MonthlyConsumptionLimit = 80m,
        MonthlyVoiceSecondsLimit = 900, MaximumConcurrentCalls = 2, MaximumCallSeconds = 90,
        AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 4,
        Hours = [new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0))],
        RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
        ServiceDefinitionsJson = "[{\"serviceKey\":\"service_a\",\"displayName\":\"Website services\",\"aliases\":[\"website\"],\"discoveryQuestion\":\"What would you like to change?\",\"publicSummary\":\"Approved service description.\"}]",
        ApprovedKnowledgeJson = "{\"answers\":[{\"key\":\"answer_a\",\"serviceKey\":\"service_a\",\"questions\":[\"What do you offer?\"],\"answer\":\"Approved answer.\",\"status\":\"approved\"}]}",
        IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"service_a\",\"crmBucket\":\"ServiceInquiry\"}],\"notifyOwner\":false}}"
    };

}
