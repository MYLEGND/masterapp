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
        var exactPrefix = original.ConversationInstructions + "\n\nFull business policy:\n" + original.BusinessPolicy +
            "\n\nContact and retention policy:\n  Exact contact instructions.\n  ";
        Assert.StartsWith(exactPrefix + "\n\nTrusted application facts for this call:\n", supplied);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(exactPrefix), System.Text.Encoding.UTF8.GetBytes(supplied[..exactPrefix.Length]));
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

