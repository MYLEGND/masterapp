using System.Text.Json;
using System.Text.Json.Nodes;
using Shared.Analytics;

namespace Infrastructure.Reception;

/// <summary>Content-only review reuses unchanged private execution authority. It never grants admission.</summary>
internal static class ReceptionistContentReview
{
    internal static void Validate(MarketingOwnerScope owner, ReceptionistBusinessConfiguration baseline,
        ReceptionistBusinessConfiguration draft)
    {
        ReceptionistConfigurationCodec.Validate(draft);
        ReceptionistApplicationServices.ValidateConfiguration(draft);
        // All unlisted dimensions must remain exactly the accepted baseline, including new future properties.
        var projected = draft with
        {
            BusinessName = baseline.BusinessName,
            CompactVoicePolicy = baseline.CompactVoicePolicy,
            CompactVoicePolicySourceSha256 = baseline.CompactVoicePolicySourceSha256,
            ConversationInstructions = baseline.ConversationInstructions,
            ApprovedKnowledgeJson = baseline.ApprovedKnowledgeJson,
            ServiceDefinitionsJson = baseline.ServiceDefinitionsJson,
            IntakeRulesJson = baseline.IntakeRulesJson
        };
        if (ReceptionistConfigurationCodec.Serialize(projected) != ReceptionistConfigurationCodec.Serialize(baseline))
            throw new InvalidOperationException("This draft changes protected operating, privacy or authority settings and requires full operator review.");
        var prior = JsonNode.Parse(baseline.IntakeRulesJson)!.AsObject();
        var next = JsonNode.Parse(draft.IntakeRulesJson)!.AsObject();
        var routes = next["canonicalInquiry"]?["services"]?.AsArray()
            ?? throw new ArgumentException("Canonical service routes are required.");
        foreach (var route in routes)
            if (!ReceptionistSetupForms.CrmBuckets(owner).Contains(route?["crmBucket"]?.GetValue<string>() ?? "", StringComparer.Ordinal))
                throw new ArgumentException("Every service requires an owner-authorized canonical inquiry destination.");
        // New/changed service mappings are explicitly included in the review; notification and other intake policy are not reusable content.
        prior["canonicalInquiry"]!.AsObject().Remove("services");
        next["canonicalInquiry"]!.AsObject().Remove("services");
        if (!JsonNode.DeepEquals(prior, next))
            throw new InvalidOperationException("Intake permissions or notification changes require full operator review.");
    }

    internal static string Receipt(string owner, string actor, Guid revision, Guid draftId, string draftHash,
        Guid baselineId, string bindingHash) => ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
        { owner, actor, revision, draftId, draftHash, baselineId, bindingHash, kind = "paused-content-review-v1" }));
}
