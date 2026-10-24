using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Data.Configurations;
using System.Reflection;
using Infrastructure.Analytics;
using Infrastructure.Reception;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistSetupAndAuthorityTests
{
    [Fact]
    public void OptionalCompactVoicePolicyPreservesDefaultAndRequiresExactSourceReview()
    {
        var original = Configuration();
        var unchanged = ReceptionistRuntimeAuthority.Ready("openai", original, RuntimeBinding(), true);
        Assert.Contains(original.BusinessPolicy, unchanged.ConversationInstructions);
        var compact = original with { CompactVoicePolicy = "Synthetic concise reviewed policy.", CompactVoicePolicySourceSha256 = ReceptionistConfigurationCodec.Hash(original.BusinessPolicy) };
        var settings = ReceptionistRuntimeAuthority.Ready("openai", compact, RuntimeBinding(), true);
        Assert.Contains(compact.CompactVoicePolicy, settings.ConversationInstructions);
        Assert.DoesNotContain(original.BusinessPolicy, settings.ConversationInstructions);
        Assert.Contains("Backchannel policy:", settings.ConversationInstructions);
        Assert.Contains("Interruption policy:", settings.ConversationInstructions);
        Assert.Contains("Delegation policy:", settings.ConversationInstructions);
        Assert.Contains("Generate <|SpawnThinking|>", settings.ConversationInstructions);
        Assert.Contains("independent record readback", settings.ConversationInstructions);
        Assert.Throws<ArgumentException>(() => ReceptionistRuntimeAuthority.Ready("openai", compact with { BusinessPolicy = "Changed source." }, RuntimeBinding(), true));
        Assert.Null(original.CompactVoicePolicy);
    }

    [Fact]
    public async Task ContentApplyCreatesOnePausedVersionAndHistoricalReplayPreservesNewerDraft()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await using var db = f.Database(); var store = f.Store(db);
        var owner = MarketingOwnerScope.Founder;
        var current = await store.GetReceptionistConfigurationAsync(owner, "openai");
        var edited = current.Configuration! with { ConversationInstructions = "Synthetic reviewed AI introduction." };
        var draft = await store.SaveReceptionistConfigurationAsync(owner, "openai", current.ConnectionRevision, edited, "actor-a");
        var preview = await store.PreviewReceptionistContentAsync(owner, "openai", "actor-a");
        Assert.True(preview.CanApply, preview.BlockReason);
        var input = new ReceptionistContentApplyInput(draft.ConnectionRevision, preview.ReviewHash, true);
        var applied = await store.ApplyReceptionistContentAsync(owner, "openai", input, "actor-a");
        Assert.False(applied.Replayed); Assert.False(applied.Current.AdmissionEnabled); Assert.True(applied.Current.BindingAccepted);
        Assert.Equal(draft.ConfigurationVersion + 1, applied.AppliedVersion);
        var enabled = await store.SetReceptionistAdmissionAsync(owner, "openai", applied.Current.ConnectionRevision, true);
        var replayWhileEnabled = await store.ApplyReceptionistContentAsync(owner, "openai", input, "actor-a");
        Assert.True(replayWhileEnabled.Replayed); Assert.True(replayWhileEnabled.Current.AdmissionEnabled);
        Assert.Equal(enabled.ConfigurationId, replayWhileEnabled.Current.ConfigurationId);
        var later = await store.SaveReceptionistConfigurationAsync(owner, "openai", enabled.ConnectionRevision,
            edited with { ConversationInstructions = "A newer unsaved-for-runtime draft." }, "actor-a");
        var replay = await store.ApplyReceptionistContentAsync(owner, "openai", input, "actor-a");
        Assert.True(replay.Replayed); Assert.Equal(applied.AppliedConfigurationId, replay.AppliedConfigurationId);
        Assert.Equal(later.ConfigurationId, replay.Current.ConfigurationId); Assert.False(replay.Current.BindingAccepted);
        await using var inspect = f.Database();
        Assert.Equal(4, await inspect.Set<ReceptionistConfigurationVersion>().CountAsync());
        var bound = await inspect.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == applied.AppliedConfigurationId);
        var binding = ReceptionistConfigurationCodec.ReadBinding(bound.AcceptedBindingJson, edited)!;
        Assert.Null(binding.OperatorAcceptancePackageSha256); Assert.Equal(preview.ReviewHash, binding.ContentReviewHash);
        Assert.Equal(f.Version.Id, binding.ContentSourceConfigurationId);
        Assert.Equal(f.Binding.ProviderProjectKey, binding.ProviderProjectKey); Assert.Equal(f.Binding.Costs, binding.Costs);
    }

    [Fact]
    public async Task ContentReviewRejectsActorCrossoverStaleStateAndProtectedChangesWithoutWriting()
    {
        await using var f = await ReadinessFixture.CreateAsync(); await using var db = f.Database(); var store = f.Store(db);
        var owner = MarketingOwnerScope.Founder;
        var current = await store.GetReceptionistConfigurationAsync(owner, "openai");
        var draft = await store.SaveReceptionistConfigurationAsync(owner, "openai", current.ConnectionRevision,
            current.Configuration! with { ConversationInstructions = "Reviewed synthetic greeting." }, "actor-a");
        var preview = await store.PreviewReceptionistContentAsync(owner, "openai", "actor-a");
        var input = new ReceptionistContentApplyInput(draft.ConnectionRevision, preview.ReviewHash, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyReceptionistContentAsync(owner, "openai", input, "actor-b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyReceptionistContentAsync(MarketingOwnerScope.Business(Guid.NewGuid()), "openai", input, "actor-a"));
        var newer = await store.SaveReceptionistConfigurationAsync(owner, "openai", draft.ConnectionRevision,
            draft.Configuration! with { MonthlyCashLimit = 50m }, "actor-a");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.ApplyReceptionistContentAsync(owner, "openai", input, "actor-a"));
        var blocked = await store.PreviewReceptionistContentAsync(owner, "openai", "actor-a"); Assert.False(blocked.CanApply);
        var actual = await store.GetReceptionistConfigurationAsync(owner, "openai"); Assert.Equal(newer.ConfigurationId, actual.ConfigurationId);
        Assert.False(actual.AdmissionEnabled);
    }

    [Fact]
    public async Task ContentRollbackCreatesInactiveDraftAndNeverRestoresHistoricalPermission()
    {
        await using var f = await ReadinessFixture.CreateAsync(); await using var db = f.Database(); var store = f.Store(db);
        var owner = MarketingOwnerScope.Founder; var current = await store.GetReceptionistConfigurationAsync(owner, "openai");
        var changed = await store.SaveReceptionistConfigurationAsync(owner, "openai", current.ConnectionRevision,
            current.Configuration! with { ConversationInstructions = "Changed greeting.", MonthlyCashLimit = 12m, CompactVoicePolicy = "New compact policy to undo.", CompactVoicePolicySourceSha256 = ReceptionistConfigurationCodec.Hash(current.Configuration.BusinessPolicy) }, "actor-a");
        var restored = await store.RestoreReceptionistContentDraftAsync(owner, "openai", new(changed.ConnectionRevision, f.Version.Id), "actor-a");
        Assert.Equal(Configuration().ConversationInstructions, restored.Configuration!.ConversationInstructions);
        Assert.Null(restored.Configuration.CompactVoicePolicy); Assert.Null(restored.Configuration.CompactVoicePolicySourceSha256);
        Assert.Equal(12m, restored.Configuration.MonthlyCashLimit); Assert.False(restored.BindingAccepted); Assert.False(restored.AdmissionEnabled);
        Assert.NotEqual(f.Version.Id, restored.ConfigurationId);
    }

    [Fact]
    public async Task ConcurrentContentApplyReusesOneImmutableResult()
    {
        await using var f = await ReadinessFixture.CreateAsync(); await using var db = f.Database(); var store = f.Store(db);
        var owner = MarketingOwnerScope.Founder; var current = await store.GetReceptionistConfigurationAsync(owner, "openai");
        var draft = await store.SaveReceptionistConfigurationAsync(owner, "openai", current.ConnectionRevision,
            current.Configuration! with { ConversationInstructions = "Concurrent synthetic greeting." }, "actor-a");
        var preview = await store.PreviewReceptionistContentAsync(owner, "openai", "actor-a");
        var input = new ReceptionistContentApplyInput(draft.ConnectionRevision, preview.ReviewHash, true);
        async Task<ReceptionistContentApplyResult> Apply()
        { await using var separate = f.Database(); return await f.Store(separate).ApplyReceptionistContentAsync(owner, "openai", input, "actor-a"); }
        var results = await Task.WhenAll(Task.Run(Apply), Task.Run(Apply));
        Assert.Single(results.Select(x => x.AppliedConfigurationId).Distinct());
        Assert.Single(results.Where(x => !x.Replayed));
        await using var inspect = f.Database(); Assert.Equal(3, await inspect.Set<ReceptionistConfigurationVersion>().CountAsync());
    }

    [Fact]
    public async Task ActualMvcFormBindingAcceptsGeneratedIdentitiesAndUntouchedAddRows()
    {
        var first = await BindOwnerFormAsync(new Dictionary<string, StringValues>
        {
            ["Services[0].ServiceKey"] = "", ["Services[0].DisplayName"] = "Website services",
            ["Services[0].Aliases"] = "website", ["Services[0].DiscoveryQuestion"] = "",
            ["Services[0].PublicSummary"] = "Approved website description.", ["Services[0].CrmBucket"] = "ServiceInquiry",
            ["Answers[0].Key"] = "", ["Answers[0].ServiceKey"] = "", ["Answers[0].Questions"] = "What do you offer?",
            ["Answers[0].Answer"] = "Approved general answer.", ["Answers[0].Status"] = "approved", ["Answers[0].AsOfDate"] = ""
        });
        var empty = Snapshot(null);
        var saved = ReceptionistSetupForms.Apply(empty, first with { ExpectedRevision = empty.ConnectionRevision });
        var serviceKey = JsonNode.Parse(saved.ServiceDefinitionsJson)![0]!["serviceKey"]!.GetValue<string>();
        var answerKey = JsonNode.Parse(saved.ApprovedKnowledgeJson)!["answers"]![0]!["key"]!.GetValue<string>();
        Assert.StartsWith("service-", serviceKey);
        Assert.StartsWith("answer-", answerKey);
        Assert.Null(JsonNode.Parse(saved.ApprovedKnowledgeJson)!["answers"]![0]!["serviceKey"]);

        var next = await BindOwnerFormAsync(new Dictionary<string, StringValues>
        {
            ["Services[0].ServiceKey"] = serviceKey, ["Services[0].DisplayName"] = "Website services",
            ["Services[0].Aliases"] = "website", ["Services[0].DiscoveryQuestion"] = "",
            ["Services[0].PublicSummary"] = "Approved website description.", ["Services[0].CrmBucket"] = "ServiceInquiry",
            ["Services[1].ServiceKey"] = "", ["Services[1].DisplayName"] = "", ["Services[1].Aliases"] = "",
            ["Services[1].DiscoveryQuestion"] = "", ["Services[1].PublicSummary"] = "", ["Services[1].CrmBucket"] = "",
            ["Answers[0].Key"] = answerKey, ["Answers[0].ServiceKey"] = "", ["Answers[0].Questions"] = "What do you offer?",
            ["Answers[0].Answer"] = "Approved general answer.", ["Answers[0].Status"] = "approved", ["Answers[0].AsOfDate"] = "",
            ["Answers[1].Key"] = "", ["Answers[1].ServiceKey"] = "", ["Answers[1].Questions"] = "",
            ["Answers[1].Answer"] = "", ["Answers[1].Status"] = "", ["Answers[1].AsOfDate"] = ""
        });
        var existing = Snapshot(saved);
        var resaved = ReceptionistSetupForms.Apply(existing, next with { ExpectedRevision = existing.ConnectionRevision });
        Assert.Single(JsonNode.Parse(resaved.ServiceDefinitionsJson)!.AsArray());
        Assert.Single(JsonNode.Parse(resaved.ApprovedKnowledgeJson)!["answers"]!.AsArray());
        Assert.Equal(serviceKey, JsonNode.Parse(resaved.ServiceDefinitionsJson)![0]!["serviceKey"]!.GetValue<string>());
        Assert.Equal(answerKey, JsonNode.Parse(resaved.ApprovedKnowledgeJson)!["answers"]![0]!["key"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => ReceptionistSetupForms.Apply(existing,
            next with { ExpectedRevision = existing.ConnectionRevision, Services = [next.Services[0] with { CrmBucket = null }] }));
        Assert.Throws<DbUpdateConcurrencyException>(() => ReceptionistSetupForms.Apply(existing, next with { ExpectedRevision = Guid.NewGuid() }));
    }

    private static async Task<ReceptionistSetupInput> BindOwnerFormAsync(Dictionary<string, StringValues> fields)
    {
        fields["BusinessName"] = "Synthetic owner";
        fields["ConversationInstructions"] = "Approved greeting.";
        fields["BusinessPolicy"] = "Approved caller policy.";
        fields["ContactAndRetentionNotes"] = "";
        fields["TimeZoneId"] = "UTC";
        fields["Currency"] = "USD";
        fields["MonthlyCashLimit"] = "25";
        fields["MonthlyConsumptionLimit"] = "25";
        fields["MonthlyVoiceMinutesLimit"] = "300";
        fields["MaximumConcurrentCalls"] = "1";
        fields["MaximumCallSeconds"] = "360";
        fields["AdmissionWindowSeconds"] = "60";
        fields["MaximumCallsPerWindow"] = "1";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvc();
        using var provider = services.BuildServiceProvider();
        var controller = new OwnerFormBindingController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = provider } }
        };
        var input = new ReceptionistSetupInput();
        var valid = await controller.TryUpdateModelAsync(input, string.Empty,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture));
        Assert.True(valid, string.Join("; ", controller.ModelState.SelectMany(x => x.Value!.Errors.Select(error => x.Key + ": " + error.ErrorMessage))));
        return input;
    }

    private sealed class OwnerFormBindingController : Controller { }

    [Fact]
    public void InactiveDraftRoundTripPreservesExactPoliciesAndOtherSavedFields()
    {
        var original = Configuration() with
        {
            ConversationInstructions = "  Owner-authored greeting.\n\n  ",
            BusinessPolicy = "  Preserve the exact approved policy.\n  ",
            ServiceDefinitionsJson = "[{\"serviceKey\":\"service_a\",\"displayName\":\"Website services\",\"aliases\":[\"website\"],\"discoveryQuestion\":\"What would you like to change?\",\"publicSummary\":\"Approved service description.\",\"retainedOwnerNote\":\"keep service note\"}]",
            ApprovedKnowledgeJson = "{\"answers\":[{\"key\":\"answer_a\",\"serviceKey\":\"service_a\",\"questions\":[\"What do you offer?\"],\"answer\":\"Approved answer.\",\"status\":\"approved\",\"retainedOwnerNote\":\"keep answer note\"}],\"retainedOwnerNote\":\"keep knowledge note\"}",
            IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"service_a\",\"crmBucket\":\"ServiceInquiry\",\"retainedOwnerNote\":\"keep route note\"}],\"notifyOwner\":false},\"retainedOwnerNote\":\"keep intake note\"}",
            ContactAndRetentionPolicyJson = "{\"policyText\":\"  Approved contact policy.\\n  \",\"retainedOwnerNote\":\"keep retention note\"}"
        };
        var snapshot = Snapshot(original);
        var result = ReceptionistSetupForms.Apply(snapshot, ReceptionistSetupForms.Read(snapshot));
        Assert.Equal(original.ConversationInstructions, result.ConversationInstructions);
        Assert.Equal(original.BusinessPolicy, result.BusinessPolicy);
        Assert.Equal(original.MonthlyVoiceSecondsLimit, result.MonthlyVoiceSecondsLimit);
        Assert.Equal(original.MonthlyCashLimit, result.MonthlyCashLimit);
        Assert.Equal(original.MonthlyConsumptionLimit, result.MonthlyConsumptionLimit);
        Assert.Equal(original.Hours, result.Hours);
        Assert.Equal("keep service note", JsonNode.Parse(result.ServiceDefinitionsJson)![0]!["retainedOwnerNote"]!.GetValue<string>());
        Assert.Equal("keep answer note", JsonNode.Parse(result.ApprovedKnowledgeJson)!["answers"]![0]!["retainedOwnerNote"]!.GetValue<string>());
        Assert.Equal("keep knowledge note", JsonNode.Parse(result.ApprovedKnowledgeJson)!["retainedOwnerNote"]!.GetValue<string>());
        Assert.Equal("keep route note", JsonNode.Parse(result.IntakeRulesJson)!["canonicalInquiry"]!["services"]![0]!["retainedOwnerNote"]!.GetValue<string>());
        Assert.Equal("keep intake note", JsonNode.Parse(result.IntakeRulesJson)!["retainedOwnerNote"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.ContactAndRetentionPolicyJson), JsonNode.Parse(result.ContactAndRetentionPolicyJson)));
    }

    [Fact]
    public void MissingSavedRowsAndStaleRevisionsCannotSilentlyReplaceConfiguration()
    {
        var snapshot = Snapshot(Configuration());
        var input = ReceptionistSetupForms.Read(snapshot);
        Assert.Throws<DbUpdateConcurrencyException>(() => ReceptionistSetupForms.Apply(snapshot, input with { ExpectedRevision = Guid.NewGuid() }));
        Assert.Throws<ArgumentException>(() => ReceptionistSetupForms.Apply(snapshot, input with { Services = [] }));
        Assert.Throws<ArgumentException>(() => ReceptionistSetupForms.Apply(snapshot, input with { Answers = [] }));
        Assert.Throws<ArgumentException>(() => ReceptionistSetupForms.Apply(snapshot, input with { Services = null! }));
        Assert.Throws<ArgumentException>(() => ReceptionistSetupForms.Apply(snapshot, input with { Hours = [null!] }));
    }

    [Fact]
    public void ExplicitRemovalAndNewServiceKeepUnrelatedSettingsAndGenerateStableReferences()
    {
        var snapshot = Snapshot(Configuration());
        var input = ReceptionistSetupForms.Read(snapshot);
        var changed = input with
        {
            Services = [input.Services[0] with { Remove = true }, new ReceptionistServiceInput
            {
                DisplayName = "Coaching", Aliases = "coaching", DiscoveryQuestion = "What would you like help with?",
                PublicSummary = "Approved coaching description.", CrmBucket = "ServiceInquiry"
            }],
            Answers = [input.Answers[0] with { Remove = true }]
        };
        var result = ReceptionistSetupForms.Apply(snapshot, changed);
        var service = JsonNode.Parse(result.ServiceDefinitionsJson)![0]!;
        var reference = service["serviceKey"]!.GetValue<string>();
        Assert.StartsWith("service-", reference);
        Assert.Equal(reference, JsonNode.Parse(result.IntakeRulesJson)!["canonicalInquiry"]!["services"]![0]!["serviceKey"]!.GetValue<string>());
        Assert.Equal(0, JsonNode.Parse(result.ApprovedKnowledgeJson)!["answers"]!.AsArray().Count);
        Assert.Equal(input.BusinessPolicy, result.BusinessPolicy);
        Assert.Equal(input.MaximumCallSeconds, result.MaximumCallSeconds);
        var reread = ReceptionistSetupForms.Read(snapshot with { Configuration = result });
        Assert.Equal(reference, reread.Services[0].ServiceKey);
    }

    [Fact]
    public void EmptyOwnerHasNoPilotLimitsHoursOrProviderAcceptance()
    {
        var snapshot = Snapshot(null);
        var input = ReceptionistSetupForms.Read(snapshot);
        Assert.Equal(0, input.MonthlyCashLimit);
        Assert.Equal(0, input.MonthlyConsumptionLimit);
        Assert.Equal(0, input.MonthlyVoiceMinutesLimit);
        Assert.Equal(0, input.MaximumConcurrentCalls);
        Assert.Equal(0, input.MaximumCallSeconds);
        Assert.All(input.Hours, x => { Assert.False(x.Enabled); Assert.Null(x.Start); Assert.Null(x.End); });
        Assert.False(snapshot.BindingAccepted); Assert.False(snapshot.AdmissionEnabled);
        Assert.Equal([Infrastructure.Leads.WorkstationLeadBuckets.ServiceInquiry], ReceptionistSetupForms.CrmBuckets(MarketingOwnerScope.Business(Guid.NewGuid())));
    }

    [Fact]
    public void PinnedCredentialMustMatchOwnerProjectAndPurposeWithoutHostFallback()
    {
        var owner = MarketingOwnerScope.Founder;
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        const string originalSecret = "  synthetic-test-credential\n  ";
        var values = new Dictionary<string, string?>
        {
            ["Receptionist:Secrets:api_ref:OwnerKey"] = owner.Key,
            ["Receptionist:Secrets:api_ref:ProviderProjectKey"] = "project_a",
            ["Receptionist:Secrets:api_ref:Kind"] = "voice-api",
            ["Receptionist:Secrets:api_ref:ProtectedValue"] = protector.Protect(owner, "voice-api", "later-host-credential")
        };
        var binding = new ReceptionistProviderBinding
        {
            CredentialReference = "api_ref", ProviderProjectKey = "project_a",
            ProtectedApiCredential = ReceptionistCredentialEnvelope.Protect(protector, owner, "openai", "project_a", "voice-api", originalSecret),
            ProtectedWebhookSecret = ReceptionistCredentialEnvelope.Protect(protector, owner, "openai", "project_a", "voice-webhook", "synthetic-webhook")
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var authority = new ReceptionistRuntimeAuthority(config, protector, TimeProvider.System);
        Assert.Equal(originalSecret, authority.Credential(owner, binding, false));
        Assert.Equal("synthetic-webhook", authority.Credential(owner, binding, true));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => authority.Credential(MarketingOwnerScope.Business(Guid.NewGuid()), binding, false));
        Assert.Throws<InvalidOperationException>(() => authority.Credential(owner, binding with { ProviderProjectKey = "other" }, false));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => authority.Credential(owner,
            binding with { ProtectedApiCredential = binding.ProtectedWebhookSecret }, false));
        Assert.Throws<InvalidOperationException>(() => authority.Credential(owner, binding with { ProtectedApiCredential = null }, false));
        var rotated = binding with
        {
            ProtectedApiCredential = ReceptionistCredentialEnvelope.Protect(protector, owner, "openai", "project_a", "voice-api", "rotated-test-credential")
        };
        Assert.Equal("rotated-test-credential", authority.Credential(owner, rotated, false));
        Assert.Equal(originalSecret, authority.Credential(owner, binding, false));
    }

    [Fact]
    public void FirstProviderRequestContainsEveryExactOwnerPolicyAndRefusesTruncation()
    {
        var original = Configuration() with
        {
            ConversationInstructions = "  An exact greeting.\n\nKeep this spacing.  ",
            BusinessPolicy = "  An exact business policy.\r\nUse only accepted actions.  ",
            ContactAndRetentionPolicyJson = "{\"policyText\":\"  Exact contact instructions.\\n  \"}"
        };
        var binding = new ReceptionistProviderBinding
        {
            IngressKey = "entry", ProviderProjectKey = "project_a", Model = "gpt-live-1", Voice = "marin",
            BackendMode = "application_services", Transport = "sip",
            Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, Infrastructure.Leads.ReceptionistInquiryAuthority.RouteKey,
                ReceptionistCapabilityStates.Accepted, "synthetic-acceptance")]
        };
        var settings = ReceptionistRuntimeAuthority.Ready("openai", original, binding, controlledTest: true);
        using var request = JsonDocument.Parse(OpenAiReceptionistProtocol.AcceptBody(settings));
        var supplied = request.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
        var exactPrefix = original.ConversationInstructions + "\n\nOwner-reviewed speech policy (not execution authority):\n" + original.BusinessPolicy +
            "\n\nContact and retention policy:\n  Exact contact instructions.\n  ";
        var ownerStart = supplied.IndexOf("Owner conversation guidance:\n", StringComparison.Ordinal);
        Assert.True(ownerStart >= 0);
        ownerStart += "Owner conversation guidance:\n".Length;
        Assert.StartsWith(exactPrefix + "\n\nTrusted application facts for this call:\n", supplied[ownerStart..]);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(exactPrefix), System.Text.Encoding.UTF8.GetBytes(supplied.Substring(ownerStart, exactPrefix.Length)));
        Assert.Contains("Call mode: controlled QA.", supplied, StringComparison.Ordinal);
        Assert.Contains("independent record readback", supplied, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ReceptionistRuntimeAuthority.Ready("openai",
            original with { BusinessPolicy = new string('x', 17000) }, binding, controlledTest: true));
        Assert.Throws<ArgumentException>(() => ReceptionistRuntimeAuthority.Ready("openai",
            original with { ContactAndRetentionPolicyJson = "{\"policyText\":[]}" }, binding, controlledTest: true));
        var atLimit = original with { BusinessPolicy = new string('x', original.BusinessPolicy.Length + OpenAiReceptionistProtocol.MaximumInstructionCharacters - supplied.Length) };
        var accepted = ReceptionistRuntimeAuthority.Ready("openai", atLimit, binding, controlledTest: true);
        Assert.Equal(OpenAiReceptionistProtocol.MaximumInstructionCharacters, accepted.ConversationInstructions.Length);
        Assert.Contains(atLimit.BusinessPolicy, accepted.ConversationInstructions, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ReceptionistRuntimeAuthority.Ready("openai",
            atLimit with { BusinessPolicy = atLimit.BusinessPolicy + "x" }, binding, controlledTest: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ActualCallModeMustMatchTheImmutableBinding(bool testOnlyBinding, bool actualControlledTest)
    {
        var binding = RuntimeBinding() with { ControlledTestOnly = testOnlyBinding, PublicActivationReceipt = "synthetic-public-acceptance" };
        Assert.Throws<InvalidOperationException>(() => ReceptionistRuntimeAuthority.Ready("openai", Configuration(), binding, actualControlledTest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void PublicModeRequiresTheActualPinnedPublicAcceptanceReceipt(string? receipt)
    {
        var binding = RuntimeBinding() with { ControlledTestOnly = false, PublicActivationReceipt = receipt };
        Assert.Throws<ArgumentException>(() => ReceptionistRuntimeAuthority.Ready("openai", Configuration(), binding, controlledTest: false));
        // Configuration-only validation cannot manufacture acceptance either.
        Assert.Throws<ArgumentException>(() => ReceptionistRuntimeAuthority.Ready("openai", Configuration(), binding));
    }

    [Fact]
    public void PublicRequestCarriesOnlyCurrentImplementedCapabilitiesWithoutPrivateIdentifiers()
    {
        var binding = RuntimeBinding() with
        {
            ControlledTestOnly = false, PublicActivationReceipt = "private-public-acceptance-value",
            ProviderProjectKey = "private_project_marker", CredentialReference = "private_api_marker",
            WebhookSecretReference = "private_webhook_marker", ProtectedApiCredential = "private_ciphertext_marker",
            Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, Infrastructure.Leads.ReceptionistInquiryAuthority.RouteKey,
                ReceptionistCapabilityStates.Accepted, "private-intake-acceptance-value"),
                new("calendar_booking", "unsupported.booking", ReceptionistCapabilityStates.Accepted, "private-unsupported-receipt")]
        };
        var original = Configuration() with { BusinessPolicy = "Historical PREPARED_NOT_LIVE policy; booking and payment require accepted routes." };
        Assert.True(ReceptionistRuntimeAuthority.Ready("openai", original, binding));
        using var request = JsonDocument.Parse(OpenAiReceptionistProtocol.AcceptBody(
            ReceptionistRuntimeAuthority.Ready("openai", original, binding, controlledTest: false)));
        var supplied = request.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
        Assert.Contains(original.BusinessPolicy, supplied, StringComparison.Ordinal);
        var overlay = supplied[(supplied.IndexOf("\n\nTrusted application facts for this call:\n", StringComparison.Ordinal))..];
        Assert.Contains("Call mode: public inbound.", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("Call mode: controlled QA.", overlay, StringComparison.Ordinal);
        Assert.Contains("confirmed inquiry intake", overlay, StringComparison.Ordinal);
        Assert.Contains("independent record readback", overlay, StringComparison.Ordinal);
        foreach (var unavailable in new[] { "calendar availability or booking", "live transfer", "outbound callback", "SMS", "email sending",
            "taking or verifying payment", "account provisioning", "authenticated customer-account access", "general CRM reads or cross-channel deduplication" })
            Assert.Contains(unavailable, overlay, StringComparison.Ordinal);
        Assert.Contains("These remain unavailable even if historical policy or other capability labels describe them.", overlay, StringComparison.Ordinal);
        foreach (var secret in new[] { binding.ProviderProjectKey, binding.CredentialReference, binding.WebhookSecretReference,
            binding.ProtectedApiCredential, binding.PublicActivationReceipt, "private-intake-acceptance-value", "private-unsupported-receipt", "unsupported.booking" })
            Assert.DoesNotContain(secret!, supplied, StringComparison.Ordinal);
        Assert.False(request.RootElement.GetProperty("session").GetProperty("store").GetBoolean());
    }

    [Fact]
    public void AnUnsupportedCanonicalRouteCannotBecomeEnabledByTheTrustedOverlay()
    {
        var binding = RuntimeBinding() with { Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, "unsupported.intake",
            ReceptionistCapabilityStates.Accepted, "synthetic-acceptance")] };
        Assert.Throws<InvalidOperationException>(() => ReceptionistRuntimeAuthority.Ready("openai", Configuration(), binding, controlledTest: true));
    }

    private static ReceptionistProviderBinding RuntimeBinding() => new()
    {
        IngressKey = "entry", ProviderProjectKey = "project_a", Model = "gpt-live-1", Voice = "marin",
        BackendMode = "application_services", Transport = "sip", ControlledTestOnly = true,
        Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, Infrastructure.Leads.ReceptionistInquiryAuthority.RouteKey,
            ReceptionistCapabilityStates.Accepted, "synthetic-acceptance")]
    };

    [Fact]
    public async Task DisabledIngressDoesNotReadCallerBodyOrResolveAdmission()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new MarketingCredentialProtector(new EphemeralDataProtectionProvider()));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ReceptionistRuntimeAuthority>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var controller = new ReceptionistIngressController(scope.ServiceProvider)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Body = new UnreadableBody();
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.Receive("entry", CancellationToken.None)).StatusCode);
    }

    [Theory]
    [InlineData(false, "disabled", "Not ready")]
    [InlineData(true, "enabled", "Unverified")]
    public async Task OwnerPermissionAndAcceptedBindingsNeverProveRuntimeOrAudioReadiness(bool enabled, string runtime, string readiness)
    {
        await using var f = await ReadinessFixture.CreateAsync();
        f.RuntimeConfiguration["Receptionist:Enabled"] = enabled.ToString();
        var before = await f.DumpAsync();
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.True(projected.OwnerPermissionEnabled);
        Assert.Equal(runtime, projected.RuntimeState);
        Assert.Equal("eligible_at_observation", projected.AdmissionReason);
        Assert.Equal("controlled_test_only", projected.BindingScope);
        Assert.Equal("valid_application_window", projected.ApplicationWindowState);
        Assert.Equal(readiness, projected.CallReadiness);
        Assert.Equal("unverified", projected.CarrierRouteState);
        Assert.Null(projected.CarrierRouteObservedUtc);
        Assert.Equal("unverified", projected.RestrictedAiAcceptanceState);
        Assert.Equal("unverified", projected.PublicAiAcceptanceState);
        Assert.Equal(before, await f.DumpAsync());
    }

    [Fact]
    public async Task SharedBusinessHostFlagsCannotImpersonateControlPlaneRuntimeOrWindowEvidence()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync());
        Assert.Equal("unknown", projected.RuntimeState);
        Assert.Equal("unknown", projected.ApplicationWindowState);
        Assert.Null(projected.OpenCallsWithoutStopCondition);
        Assert.Null(projected.AwaitingStopOrClosure);
        Assert.Equal("Unverified", projected.CallReadiness);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-project")]
    [InlineData("not-restricted")]
    [InlineData("overlong")]
    public async Task MissingStaleOrMismatchedWindowNeverMeansRestrictedRouteReady(string state)
    {
        await using var f = await ReadinessFixture.CreateAsync();
        const string prefix = "Receptionist:AcceptanceWindows:entry:";
        if (state == "missing") f.RuntimeConfiguration[prefix + "AuthorizationReceipt"] = null;
        if (state == "expired") f.RuntimeConfiguration[prefix + "ExpiresUtc"] = f.Now.ToString("O");
        if (state == "future") f.RuntimeConfiguration[prefix + "StartsUtc"] = f.Now.AddMinutes(1).ToString("O");
        if (state == "wrong-owner") f.RuntimeConfiguration[prefix + "OwnerKey"] = MarketingOwnerScope.Business(Guid.NewGuid()).Key;
        if (state == "wrong-project") f.RuntimeConfiguration[prefix + "ProviderProjectKey"] = "different_project";
        if (state == "not-restricted") f.RuntimeConfiguration[prefix + "VerifiedRestrictedTestRoute"] = "false";
        if (state == "overlong") f.RuntimeConfiguration[prefix + "ExpiresUtc"] = f.Now.AddHours(2).ToString("O");
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.Equal("not_verified", projected.ApplicationWindowState);
        Assert.Equal("Not ready", projected.CallReadiness);
        Assert.Equal("unverified", projected.CarrierRouteState);
        Assert.Equal("unverified", projected.RestrictedAiAcceptanceState);
    }

    [Fact]
    public async Task PublicBindingAndClosedAcceptedProviderStateDoNotFabricateHumanAudioAcceptance()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await f.ChangeBindingAsync(f.Binding with { ControlledTestOnly = false, PublicActivationReceipt = "synthetic-public-receipt" });
        await using (var db = f.Database())
        {
            var call = f.Call("closed"); call.State = "closed"; call.ProviderStateReceipt = "synthetic-accepted-then-closed";
            call.ProviderClosedUtc = f.Now.AddSeconds(-1); db.Add(call);
            await db.SaveChangesAsync();
        }
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.Equal("public", projected.BindingScope);
        Assert.Equal("not_applicable", projected.ApplicationWindowState);
        Assert.Equal("Unverified", projected.CallReadiness);
        Assert.Equal("unverified", projected.PublicAiAcceptanceState);
        Assert.Equal(0, projected.OpenCalls);

    }

    [Theory]
    [InlineData("revision")]
    [InlineData("configuration")]
    [InlineData("owner")]
    [InlineData("hash")]
    public async Task StaleOrForgedSnapshotCannotProjectAnotherTenantOrAffirmativeFacts(string change)
    {
        await using var f = await ReadinessFixture.CreateAsync();
        var snapshot = await f.SnapshotAsync();
        snapshot = change switch
        {
            "revision" => snapshot with { ConnectionRevision = Guid.NewGuid() },
            "configuration" => snapshot with { ConfigurationId = Guid.NewGuid() },
            "owner" => snapshot with { Owner = MarketingOwnerScope.Business(Guid.NewGuid()) },
            _ => snapshot with { ConfigurationSha256 = new string('0', 64) }
        };
        var projected = await f.Service.GetSetupReadinessAsync(snapshot, observeControlPlaneRuntime: true);
        Assert.Null(projected.OwnerPermissionEnabled);
        Assert.False(projected.CanAllowOwnerPermission);
        Assert.Null(projected.OpenCalls);
        Assert.Equal("unknown", projected.RuntimeState);
        Assert.Equal("Unverified", projected.CallReadiness);
    }

    [Theory]
    [InlineData("admission_disabled")]
    [InlineData("outside_reception_hours")]
    [InlineData("accounting_disabled_or_mismatched")]
    [InlineData("unreconciled_accounting_period")]
    [InlineData("owner_cash_limit")]
    [InlineData("owner_consumption_limit")]
    [InlineData("funding_cash_limit")]
    [InlineData("funding_consumption_limit")]
    [InlineData("owner_voice_minutes_limit")]
    [InlineData("owner_concurrency_limit")]
    [InlineData("owner_admission_rate_limit")]
    public async Task ReadOnlyProjectionAndActualReservationReturnTheSameCanonicalFirstBlocker(string expected)
    {
        await using var f = await ReadinessFixture.CreateAsync();
        if (expected == "outside_reception_hours") await f.ChangeConfigurationAsync(Configuration() with { Hours = [] });
        await using (var db = f.Database())
        {
            if (expected == "admission_disabled")
                (await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled = false;
            if (expected == "accounting_disabled_or_mismatched")
                (await db.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == f.FundingAccount.Id)).AdmissionEnabled = false;
            if (expected == "unreconciled_accounting_period")
                (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == f.FundingAccount.Id)).OpeningReconciled = false;
            if (expected is "owner_cash_limit" or "funding_cash_limit")
                (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId ==
                    (expected == "owner_cash_limit" ? f.OwnerAccount.Id : f.FundingAccount.Id))).CashCommitted = 40m;
            if (expected is "owner_consumption_limit" or "funding_consumption_limit")
                (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId ==
                    (expected == "owner_consumption_limit" ? f.OwnerAccount.Id : f.FundingAccount.Id))).ConsumptionCommitted = 80m;
            if (expected == "owner_voice_minutes_limit")
                (await db.Set<ReceptionistBudgetPeriod>().SingleAsync(x => x.BudgetAccountId == f.OwnerAccount.Id)).VoiceSecondsCommitted = 900;
            if (expected == "owner_concurrency_limit")
                for (var i = 0; i < 2; i++) db.Add(f.Call("open-" + i));
            if (expected == "owner_admission_rate_limit")
                for (var i = 0; i < 4; i++)
                {
                    var call = f.Call("recent-" + i); call.State = "closed"; call.ProviderClosedUtc = f.Now.AddSeconds(-1); call.VoiceUsageFinal = true; db.Add(call);
                }
            await db.SaveChangesAsync();
        }
        var before = await f.DumpAsync();
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.Equal(expected, projected.AdmissionReason);
        Assert.Equal("Not ready", projected.CallReadiness);
        Assert.Equal(before, await f.DumpAsync());
        var admission = await f.Service.ReserveAsync(f.Ingress("actual"));
        Assert.False(admission.Reserved);
        Assert.Equal(expected, admission.Reason);
    }

    [Fact]
    public async Task RolloverHoldIsReadOnlyInProjectionButPersistedByTheOriginalReservationTransaction()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        ReceptionistCall boundary;
        await using (var db = f.Database())
        {
            var previous = f.Period(f.OwnerAccount.Id, f.Now.Date.AddMonths(-1), f.Now.Date);
            previous.MonthKey = 202609; db.Add(previous);
            boundary = f.Call("prior-period"); boundary.OwnerBudgetPeriodId = previous.Id;
            boundary.ReservedUtc = f.Now.Date.AddSeconds(-30); db.Add(boundary);
            await db.SaveChangesAsync();
        }
        var before = await f.DumpAsync();
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync());
        Assert.Equal("voice_allocation_unreconciled", projected.AdmissionReason);
        Assert.Equal(before, await f.DumpAsync());
        var admission = await f.Service.ReserveAsync(f.Ingress("after-boundary"));
        Assert.Equal("voice_allocation_unreconciled", admission.Reason);
        await using var read = f.Database();
        Assert.True((await read.Set<ReceptionistCall>().SingleAsync(x => x.Id == boundary.Id)).VoiceAllocationUncertain);
        Assert.False((await read.Set<ReceptionistBudgetAccount>().SingleAsync(x => x.Id == f.OwnerAccount.Id)).AdmissionEnabled);
        Assert.Equal(1, await read.Set<ReceptionistCall>().CountAsync());
    }

    [Fact]
    public async Task ProjectionEligibilityCannotAuthorizeLaterReservationAndTenantCountsRemainScoped()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await using (var db = f.Database())
        {
            var business = new CommerceBusiness { Id = Guid.NewGuid() }; db.Add(business);
            var other = new MarketingConnection { OwnerType = "business", OwnerKey = MarketingOwnerScope.Business(business.Id).Key,
                CommerceBusinessId = business.Id, Provider = "openai" }; db.Add(other);
            await db.SaveChangesAsync();
            var otherVersion = new ReceptionistConfigurationVersion { MarketingConnectionId = other.Id, Version = 1,
                ConfigurationJson = f.Version.ConfigurationJson, ConfigurationSha256 = f.Version.ConfigurationSha256,
                CreatedByActorId = "synthetic-other-actor", CreatedUtc = f.Now };
            var otherAccount = new ReceptionistBudgetAccount { Kind = "owner", ScopeKey = other.OwnerKey, Currency = "USD",
                TimeZoneId = "UTC", AdmissionEnabled = true };
            db.AddRange(otherVersion, otherAccount); await db.SaveChangesAsync();
            var otherPeriod = f.Period(otherAccount.Id, new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
            db.Add(otherPeriod); await db.SaveChangesAsync();
            var unrelated = f.Call("other-owner"); unrelated.MarketingConnectionId = other.Id;
            unrelated.ConfigurationVersionId = otherVersion.Id; unrelated.OwnerBudgetAccountId = otherAccount.Id;
            unrelated.OwnerBudgetPeriodId = otherPeriod.Id; unrelated.ConnectionControlRevision = other.ReceptionistControlRevision;
            unrelated.OwnerControlRevision = otherAccount.ControlRevision; db.Add(unrelated); await db.SaveChangesAsync();
        }
        var snapshot = await f.SnapshotAsync();
        var projected = await f.Service.GetSetupReadinessAsync(snapshot);
        Assert.Equal("eligible_at_observation", projected.AdmissionReason);
        Assert.Equal(0, projected.OpenCalls);
        await using (var db = f.Database())
        {
            (await db.MarketingConnections.SingleAsync(x => x.Id == f.Connection.Id)).ReceptionistAdmissionEnabled = false;
            await db.SaveChangesAsync();
        }
        var admission = await f.Service.ReserveAsync(f.Ingress("after-permission-change"));
        Assert.False(admission.Reserved);
        Assert.Equal("admission_disabled", admission.Reason);
    }

    [Fact]
    public async Task ReservationRetainsOwnerClassificationIntakeAndHoursDenialOrder()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await f.ChangeConfigurationAsync(Configuration() with { Hours = [] });
        await f.ChangeBindingAsync(f.Binding with { Capabilities = [] });
        await using (var db = f.Database())
        { (await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled = false; await db.SaveChangesAsync(); }
        Assert.Equal("admission_disabled", (await f.Service.ReserveAsync(f.Ingress("owner-first") with { ControlledTest = false })).Reason);
        await using (var db = f.Database())
        { (await db.MarketingConnections.SingleAsync()).ReceptionistAdmissionEnabled = true; await db.SaveChangesAsync(); }
        Assert.Equal("controlled_acceptance_only", (await f.Service.ReserveAsync(f.Ingress("classification-next") with { ControlledTest = false })).Reason);
        Assert.Equal("canonical_intake_unaccepted", (await f.Service.ReserveAsync(f.Ingress("intake-next"))).Reason);
        await f.ChangeBindingAsync(f.Binding with { Capabilities = RuntimeBinding().Capabilities });
        Assert.Equal("outside_reception_hours", (await f.Service.ReserveAsync(f.Ingress("hours-next"))).Reason);
    }

    [Fact]
    public async Task ExactSeparateCashAndConsumptionCeilingsStillAllowTheSameReservation()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await using (var db = f.Database())
        {
            foreach (var period in await db.Set<ReceptionistBudgetPeriod>().ToListAsync())
            { period.CashCommitted = 38m; period.ConsumptionCommitted = 77m; }
            await db.SaveChangesAsync();
        }
        Assert.Equal("eligible_at_observation", (await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync())).AdmissionReason);
        var admission = await f.Service.ReserveAsync(f.Ingress("equal-ceilings"));
        Assert.True(admission.Reserved);
        Assert.Equal(f.Now.AddSeconds(90), admission.MustStopUtc);
        await using var read = f.Database();
        var cost = await read.Set<ReceptionistCostReservation>().SingleAsync();
        Assert.Equal(2m, cost.CashReserved); Assert.Equal(3m, cost.ConsumptionReserved);
    }

    [Fact]
    public async Task RateWindowRetainsInclusiveBoundaryAndFreshChecksAfterObservation()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await f.ChangeConfigurationAsync(Configuration() with { MaximumCallsPerWindow = 1 });
        Guid previousId;
        await using (var db = f.Database())
        {
            var call = f.Call("exact-window-boundary"); call.State = "closed";
            call.ReservedUtc = f.Now.AddSeconds(-60); call.ProviderClosedUtc = f.Now.AddSeconds(-1); call.VoiceUsageFinal = true;
            previousId = call.Id; db.Add(call); await db.SaveChangesAsync();
        }
        Assert.Equal("owner_admission_rate_limit", (await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync())).AdmissionReason);
        Assert.Equal("owner_admission_rate_limit", (await f.Service.ReserveAsync(f.Ingress("at-boundary"))).Reason);
        await using (var db = f.Database())
        { (await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == previousId)).ReservedUtc = f.Now.AddSeconds(-60).AddTicks(-1); await db.SaveChangesAsync(); }
        Assert.True((await f.Service.ReserveAsync(f.Ingress("outside-boundary"))).Reserved);
    }

    [Theory]
    [InlineData(true, 2, 1)]
    [InlineData(false, 0, 3)]
    public async Task ReservedAndUncertainOpenRecordsAreNotLabeledVerifiedLiveActivity(bool enabled, int withoutStop, int closing)
    {
        await using var f = await ReadinessFixture.CreateAsync();
        f.RuntimeConfiguration["Receptionist:Enabled"] = enabled.ToString();
        await using (var db = f.Database())
        {
            var stopping = f.Call("stopping"); stopping.ReservedUtc = f.Now.AddSeconds(-2); stopping.MustStopUtc = f.Now.AddSeconds(-1);
            var uncertain = f.Call("uncertain"); uncertain.State = "uncertain";
            db.AddRange(stopping, f.Call("reserved"), uncertain); await db.SaveChangesAsync();
        }
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.Equal(3, projected.OpenCalls); Assert.Equal(withoutStop, projected.OpenCallsWithoutStopCondition); Assert.Equal(closing, projected.AwaitingStopOrClosure);
        await using var read = f.Database();
        Assert.All(await read.Set<ReceptionistCall>().ToListAsync(), call => Assert.Null(call.ProviderClosedUtc));
    }

    [Fact]
    public async Task MissingBindingAndFallbackReceiptCannotBecomeVerifiedRouteEvidence()
    {
        await using var f = await ReadinessFixture.CreateAsync();
        await using (var db = f.Database())
        { (await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson = null; await db.SaveChangesAsync(); }
        var projected = await f.Service.GetSetupReadinessAsync(await f.SnapshotAsync(), observeControlPlaneRuntime: true);
        Assert.Equal("unaccepted", projected.BindingScope); Assert.False(projected.CanAllowOwnerPermission);
        Assert.Equal("configuration_or_binding_unavailable", projected.AdmissionReason);
        Assert.Equal("unverified", projected.CarrierRouteState); Assert.Equal("Not ready", projected.CallReadiness);
    }

    private sealed class ReadinessClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    // This synthetic relational fixture reuses the exact production mappings, never a provider or live owner.
    private sealed class ReadinessFixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "readiness-" + Guid.NewGuid().ToString("N") + ".sqlite");
        internal DateTime Now { get; } = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        internal DbContextOptions<MasterAppDbContext> Options { get; private set; } = null!;
        internal IConfigurationRoot RuntimeConfiguration { get; private set; } = null!;
        private ServiceProvider services = null!;
        private MarketingCredentialProtector protector = null!;
        internal MarketingConnection Connection { get; } = new() { OwnerKey = MarketingOwnerScope.Founder.Key, OwnerType = "founder", Provider = "openai", ReceptionistAdmissionEnabled = true };
        internal ReceptionistBudgetAccount OwnerAccount { get; } = new() { Kind = "owner", ScopeKey = MarketingOwnerScope.Founder.Key, Currency = "USD", TimeZoneId = "UTC", AdmissionEnabled = true };
        internal ReceptionistBudgetAccount FundingAccount { get; } = new() { Kind = "funding", ScopeKey = "synthetic_funding", Currency = "USD", TimeZoneId = "UTC", AdmissionEnabled = true };
        internal ReceptionistConfigurationVersion Version { get; } = new();
        internal ReceptionistProviderBinding Binding { get; private set; } = null!;
        internal Guid OwnerPeriodId { get; private set; }
        internal ReceptionistAdmissionService Service => new(Options, new ReadinessClock(Now), services);
        internal MasterAppDbContext Database() => new(Options);
        internal MarketingConnectionStore Store(MasterAppDbContext db) => new(db, protector);

        internal static async Task<ReadinessFixture> CreateAsync()
        {
            var f = new ReadinessFixture();
            var connection = "Data Source=" + f.path + ";Pooling=False";
            await using var schema = new ReadinessSchemaContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);
            f.Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).UseModel(schema.Model).Options;
            await schema.Database.EnsureCreatedAsync();
            f.Binding = RuntimeBinding() with
            {
                CredentialReference = "synthetic-api-reference", WebhookSecretReference = "synthetic-webhook-reference",
                ProviderVerificationReceipt = "synthetic-provider", ExclusiveProjectVerificationReceipt = "synthetic-exclusive",
                CostVerificationReceipt = "synthetic-cost", DisclosureConfigurationReceipt = "synthetic-disclosure",
                RetentionConfigurationReceipt = "synthetic-retention", CarrierFallbackReceipt = "synthetic-fallback",
                MaximumSupportedCallSeconds = 90, Costs = [new("synthetic_cost", f.FundingAccount.Id, 2m, 0m, 3m, 0m)]
            };
            f.Version.MarketingConnectionId = f.Connection.Id; f.Version.Version = 17;
            f.Version.CreatedByActorId = "synthetic-actor"; f.Version.CreatedUtc = f.Now.AddDays(-1);
            f.Version.ConfigurationJson = ReceptionistConfigurationCodec.Serialize(Configuration());
            f.Version.ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(f.Version.ConfigurationJson);
            f.Version.AcceptedBindingJson = JsonSerializer.Serialize(f.Binding, ReceptionistConfigurationCodec.Json);
            f.Connection.ReceptionistActiveConfigurationId = null;
            f.Connection.ReceptionistIngressKey = f.Binding.IngressKey; f.Connection.ReceptionistProviderProjectKey = f.Binding.ProviderProjectKey;
            await using (var db = f.Database())
            {
                db.AddRange(f.Connection, f.OwnerAccount, f.FundingAccount); await db.SaveChangesAsync();
                db.Add(f.Version); await db.SaveChangesAsync();
                f.Connection.ReceptionistActiveConfigurationId = f.Version.Id;
                var ownerPeriod = f.Period(f.OwnerAccount.Id, new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
                f.OwnerPeriodId = ownerPeriod.Id;
                db.AddRange(ownerPeriod, f.Period(f.FundingAccount.Id, ownerPeriod.StartUtc, ownerPeriod.EndUtc));
                await db.SaveChangesAsync();
            }
            const string prefix = "Receptionist:AcceptanceWindows:entry:";
            f.RuntimeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Receptionist:Enabled"] = "true", [prefix + "OwnerKey"] = MarketingOwnerScope.Founder.Key,
                [prefix + "ProviderProjectKey"] = f.Binding.ProviderProjectKey, [prefix + "VerifiedRestrictedTestRoute"] = "true",
                [prefix + "AuthorizationReceipt"] = "synthetic-authorization", [prefix + "StartsUtc"] = f.Now.AddMinutes(-1).ToString("O"),
                [prefix + "ExpiresUtc"] = f.Now.AddMinutes(30).ToString("O")
            }).Build();
            f.protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
            var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(f.RuntimeConfiguration);
            services.AddSingleton(f.protector); services.AddSingleton<TimeProvider>(new ReadinessClock(f.Now));
            services.AddScoped<ReceptionistRuntimeAuthority>(); f.services = services.BuildServiceProvider();
            return f;
        }

        internal ReceptionistBudgetPeriod Period(Guid accountId, DateTime start, DateTime end) => new()
        {
            BudgetAccountId = accountId, MonthKey = 202610, StartUtc = start, EndUtc = end,
            CashLimit = 40m, ConsumptionLimit = 80m, OpeningReconciled = true, OpeningReceipt = "synthetic-opening",
            CeilingApprovalReceipt = "synthetic-ceiling", ReconciledThroughUtc = end <= Now ? end.AddTicks(-1) : Now.AddSeconds(-1)
        };

        internal ReceptionistCall Call(string session) => new()
        {
            MarketingConnectionId = Connection.Id, ConfigurationVersionId = Version.Id, OwnerBudgetAccountId = OwnerAccount.Id,
            OwnerBudgetPeriodId = OwnerPeriodId, ConnectionControlRevision = Connection.ReceptionistControlRevision,
            OwnerControlRevision = OwnerAccount.ControlRevision, Provider = "openai", IngressKey = Binding.IngressKey,
            ProviderProjectKey = Binding.ProviderProjectKey, ProviderSessionId = session, ReservedUtc = Now.AddSeconds(-1),
            MustStopUtc = Now.AddSeconds(90), ReservedVoiceSeconds = 90
        };

        internal TrustedReceptionistIngress Ingress(string session) => new("openai", Binding.IngressKey, Binding.ProviderProjectKey,
            session, "synthetic-event-" + session, Binding.ControlledTestOnly, Connection.Id, Version.Id, MarketingOwnerScope.Founder.Key);

        internal async Task ChangeBindingAsync(ReceptionistProviderBinding binding)
        {
            Binding = binding; await using var db = Database();
            (await db.Set<ReceptionistConfigurationVersion>().SingleAsync()).AcceptedBindingJson = JsonSerializer.Serialize(binding, ReceptionistConfigurationCodec.Json);
            await db.SaveChangesAsync();
        }

        internal async Task ChangeConfigurationAsync(ReceptionistBusinessConfiguration configuration)
        {
            await using var db = Database(); var version = await db.Set<ReceptionistConfigurationVersion>().SingleAsync();
            version.ConfigurationJson = ReceptionistConfigurationCodec.Serialize(configuration);
            version.ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(version.ConfigurationJson); await db.SaveChangesAsync();
        }

        internal async Task<ReceptionistConfigurationSnapshot> SnapshotAsync()
        {
            await using var db = Database(); var connection = await db.MarketingConnections.SingleAsync(x => x.Id == Connection.Id);
            var version = await db.Set<ReceptionistConfigurationVersion>().SingleAsync(x => x.Id == Version.Id);
            var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
            return new(MarketingOwnerScope.Founder, "openai", connection.Revision, version.Id, version.Version, version.ConfigurationSha256,
                connection.ReceptionistAdmissionEnabled, true, configuration, [], new(true, true, 40m, 80m, true, 0m, 0m, 0m, 0m));
        }

        internal async Task<string> DumpAsync()
        {
            await using var db = Database();
            return JsonSerializer.Serialize(new
            {
                Connections = await db.MarketingConnections.OrderBy(x => x.Id).ToListAsync(),
                Versions = await db.Set<ReceptionistConfigurationVersion>().OrderBy(x => x.Id).ToListAsync(),
                Accounts = await db.Set<ReceptionistBudgetAccount>().OrderBy(x => x.Id).ToListAsync(),
                Periods = await db.Set<ReceptionistBudgetPeriod>().OrderBy(x => x.Id).ToListAsync(),
                Calls = await db.Set<ReceptionistCall>().OrderBy(x => x.Id).ToListAsync(),
                Costs = await db.Set<ReceptionistCostReservation>().OrderBy(x => x.Id).ToListAsync(),
                Decisions = await db.Set<ReceptionistSessionDecision>().OrderBy(x => x.Id).ToListAsync(),
                Events = await db.Set<ReceptionistIngressEvent>().OrderBy(x => x.Id).ToListAsync(),
                Actions = await db.Set<ReceptionistActionIntent>().OrderBy(x => x.Id).ToListAsync()
            });
        }

        public async ValueTask DisposeAsync()
        { if (services is not null) await services.DisposeAsync(); protector?.Dispose(); File.Delete(path); }
    }

    private sealed class ReadinessSchemaContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new MarketingConnectionConfiguration());
            ReceptionistModelConfiguration.Configure(model, Database.ProviderName);
            foreach (var type in new[] { typeof(AgentTrackingProfile), typeof(CommerceBusiness) })
            {
                var entity = model.Entity(type);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name != "Id")) entity.Ignore(property.Name);
                entity.HasKey("Id");
            }
            var allowed = new HashSet<Type>(typeof(ReceptionistCall).Assembly.GetTypes().Where(x => x.Namespace == "Domain.Entities" && x.Name.StartsWith("Receptionist", StringComparison.Ordinal)))
                { typeof(MarketingConnection), typeof(AgentTrackingProfile), typeof(CommerceBusiness) };
            foreach (var entity in model.Model.GetEntityTypes().ToArray())
                if (!allowed.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
        }
    }

    private static ReceptionistConfigurationSnapshot Snapshot(ReceptionistBusinessConfiguration? config) =>
        new(MarketingOwnerScope.Founder, "openai", Guid.NewGuid(), config is null ? null : Guid.NewGuid(),
            config is null ? 0 : 1, null, false, false, config, []);

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

    private sealed class UnreadableBody : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("A disabled ingress must not read its body.");
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
