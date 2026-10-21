using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Entities;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

// Included by the existing WebsiteContentEditorRoundTripTests CI filter.
public sealed class WebsiteContentEditorRoundTripTestsRuntimeAnchorRecovery
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Draft(string path, string pageKey, string nativeId)
    {
        var document = new WebsiteContentDocument
        {
            Pages = new(StringComparer.Ordinal)
            {
                [path] = new WebsitePageDocument
                {
                    Title = "Saved page", Description = "Saved description",
                    SystemTemplateKey = WebsiteSystemTemplateAuthority.Resolve("protect", path),
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "signal:" + pageKey + ".form." + nativeId + ".0",
                            Type = "text", Tag = "p", Text = "Original custom labels and evidence",
                            ClassName = "pcq-form-frame",
                            Style = new WebsiteVisualStyle { OffsetXPercent = 0 },
                            Layout = new WebsiteCompositionLayout { Mode = "free", Direction = "column" }
                        }
                    ]
                }
            }
        };
        return JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(document), Options);
    }

    [Theory]
    [InlineData("/RiskAssessment", "risk_assessment", "riskassessmentform")]
    [InlineData("/Quote/Life", "quote_life", "lifewizardform")]
    [InlineData("/Quote/Whole-Life", "quote_whole_life", "lifewizardform")]
    [InlineData("/Quote/Term-Life", "quote_term_life", "lifewizardform")]
    [InlineData("/Quote/Final-Expense", "quote_final_expense", "lifewizardform")]
    [InlineData("/Quote/Mortgage-Protection", "quote_mortgage_protection", "lifewizardform")]
    [InlineData("/Quote/IUL", "quote_iul", "lifewizardform")]
    [InlineData("/Quote/Auto", "quote_auto", "autoquoteform")]
    [InlineData("/Quote/Home", "quote_home", "homequoteform")]
    [InlineData("/Quote/Commercial", "quote_commercial", "commercialquoteform")]
    [InlineData("/Quote/Disability", "quote_disability", "disabilityquoteform")]
    [InlineData("/Quote/Dental-Vision-Hearing", "quote_dvh", "dvhquoteform")]
    [InlineData("/Quote/Life/landing", "quote_life_landing", "lifewizardform")]
    [InlineData("/Quote/Whole-Life/landing", "quote_whole_life_landing", "lifewizardform")]
    [InlineData("/Quote/Term-Life/landing", "quote_term_life_landing", "lifewizardform")]
    [InlineData("/Quote/Final-Expense/landing", "quote_final_expense_landing", "lifewizardform")]
    [InlineData("/Quote/Mortgage-Protection/landing", "quote_mortgage_protection_landing", "lifewizardform")]
    [InlineData("/Quote/IUL/landing", "quote_iul_landing", "lifewizardform")]
    [InlineData("/Quote/Disability/landing", "quote_disability_landing", "disabilityquoteform")]
    [InlineData("/Quote/Dental-Vision-Hearing/landing", "quote_dvh_landing", "dvhquoteform")]
    public void No_duplicate_recovery_changes_only_three_reviewed_properties(
        string path, string pageKey, string nativeId)
    {
        var original = Draft(path, pageKey, nativeId);
        var source = WebsiteContentSanitizer.ReadPersisted(original, Options);
        Assert.True(WebsiteSystemTemplateAuthority.HasReviewableRuntimeAnchorRecovery("protect", source));
        var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(original, Options, "protect");
        Assert.Empty(plan.Changes);
        Assert.Empty(plan.PresentationChanges);
        var change = Assert.Single(plan.RuntimeAnchorChanges);
        Assert.Equal(path, change.PagePath);
        var expected = JsonNode.Parse(original)!;
        var node = expected["pages"]![path]!["composition"]![0]!;
        node["type"] = "container";
        node["tag"] = "div";
        node["systemKey"] = WebsiteSystemTemplateAuthority.RuntimeFormKey(path);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(plan.DocumentJson)));
        var roundTrip = WebsiteContentSanitizer.ReadPersisted(plan.DocumentJson, Options);
        Assert.False(WebsiteSystemTemplateAuthority.HasReviewableRuntimeAnchorRecovery("protect", roundTrip));
        Assert.Equal(plan.RepairedFingerprint,
            WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(original, Options, "protect").RepairedFingerprint);
        Assert.Throws<ArgumentException>(() =>
            WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(plan.DocumentJson, Options, "protect"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("behavior")]
    [InlineData("child")]
    [InlineData("unknown-field")]
    public void Ambiguous_or_protected_projection_is_not_repaired(string corruption)
    {
        var raw = JsonNode.Parse(Draft("/Quote/Auto", "quote_auto", "autoquoteform"))!;
        var nodes = raw["pages"]!["/Quote/Auto"]!["composition"]!.AsArray();
        var node = nodes[0]!;
        switch (corruption)
        {
            case "missing": node["id"] = "authored-placeholder"; break;
            case "foreign": node["systemKey"] = "protect_runtime_form:quote_home_form"; break;
            case "behavior": node["systemBinding"] = "business_name"; break;
            case "child": node["children"]!.AsArray().Add(node.DeepClone()); break;
            case "unknown-field": node["unknownRecoveryValue"] = "retain"; break;
        }
        var original = raw.ToJsonString();
        Assert.ThrowsAny<ArgumentException>(() =>
            WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(original, Options, "protect"));
        Assert.Equal(original, raw.ToJsonString());
    }

    [Theory]
    [InlineData("/Quote/Auto/landing", "quote_auto_landing", "autoquoteform")]
    [InlineData("/a/example/Quote/Auto", "quote_auto", "autoquoteform")]
    public void Unverified_effective_form_aliases_require_review(string path, string pageKey, string nativeId)
    {
        var original = Draft(path, pageKey, nativeId);
        var document = WebsiteContentSanitizer.ReadPersisted(original, Options);
        Assert.False(WebsiteSystemTemplateAuthority.HasReviewableRuntimeAnchorRecovery("protect", document));
        Assert.Throws<ArgumentException>(() =>
            WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(original, Options, "protect"));
    }

    [Fact]
    public void Ambiguous_historical_nodes_can_repair_ids_without_inventing_a_runtime_binding()
    {
        var raw = JsonNode.Parse(Draft("/Quote/Auto", "quote_auto", "autoquoteform"))!;
        var nodes = raw["pages"]!["/Quote/Auto"]!["composition"]!.AsArray();
        nodes.Add(nodes[0]!.DeepClone());
        var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(raw.ToJsonString(), Options, "protect");
        Assert.Single(plan.Changes);
        Assert.Empty(plan.RuntimeAnchorChanges);
    }

    [Fact]
    public void Existing_duplicate_recovery_does_not_require_repairable_runtime_shapes()
    {
        var raw = JsonNode.Parse(Draft("/Quote/Auto", "quote_auto", "autoquoteform"))!;
        var nodes = raw["pages"]!["/Quote/Auto"]!["composition"]!.AsArray();
        nodes[0]!["id"] = "original-presentation";
        nodes.Add(nodes[0]!.DeepClone());
        var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(raw.ToJsonString(), Options, "protect");
        Assert.Single(plan.Changes);
        Assert.Empty(plan.RuntimeAnchorChanges);
        _ = WebsiteContentSanitizer.ReadPersisted(plan.DocumentJson, Options);
    }

    [Fact]
    public void Multiple_active_pages_are_repaired_in_one_plan()
    {
        var raw = JsonNode.Parse(Draft("/Quote/Auto", "quote_auto", "autoquoteform"))!;
        var home = JsonNode.Parse(Draft("/Quote/Home", "quote_home", "homequoteform"))!;
        raw["pages"]!["/Quote/Home"] = home["pages"]!["/Quote/Home"]!.DeepClone();
        var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(raw.ToJsonString(), Options, "protect");
        Assert.Equal(2, plan.RuntimeAnchorChanges.Count);
        var repaired = WebsiteContentSanitizer.ReadPersisted(plan.DocumentJson, Options);
        Assert.False(WebsiteSystemTemplateAuthority.HasReviewableRuntimeAnchorRecovery("protect", repaired));
    }

    [Fact]
    public void Other_site_does_not_acquire_protected_runtime_binding()
    {
        var original = Draft("/Quote/Auto", "quote_auto", "autoquoteform");
        Assert.Throws<ArgumentException>(() =>
            WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(original, Options, "business"));
    }

    [Theory]
    [InlineData("/Quote/Life/landing", "protect_runtime_form:quote_life_landing")]
    [InlineData("/quote/disability/landing", "protect_runtime_form:quote_disability_landing_form")]
    [InlineData("/Quote/Dental-Vision-Hearing/landing", "protect_runtime_form:quote_dvh_landing_form")]
    [InlineData("/Quote/Life", "protect_runtime_form:quote_life")]
    public void Runtime_authority_matches_native_form_key(string path, string expected)
    {
        Assert.Equal(expected, WebsiteSystemTemplateAuthority.RuntimeFormKey(path));
    }

    [Fact]
    public void Published_lookup_keeps_base_and_landing_native_identities_separate()
    {
        var raw = JsonNode.Parse(Draft("/Quote/Life", "quote_life", "lifewizardform"))!;
        var landing = JsonNode.Parse(Draft("/Quote/Life/landing", "quote_life_landing", "lifewizardform"))!;
        raw["pages"]!["/Quote/Life/landing"] = landing["pages"]!["/Quote/Life/landing"]!.DeepClone();
        var plan = WebsiteContentSanitizer.PlanDuplicateIdentityRecovery(raw.ToJsonString(), Options, "protect");
        var version = new WebsiteContentVersion { DocumentJson = plan.DocumentJson };
        var baseId = "signal:quote_life.form.lifewizardform.0";
        var landingId = "signal:quote_life_landing.form.lifewizardform.0";
        Assert.Equal(baseId, WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "quote_life"));
        Assert.Equal(landingId, WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "quote_life_landing"));
        Assert.Equal(landingId, WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "/Quote/Life/landing"));
        Assert.Equal(landingId, WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "/a/example/Quote/Life/landing"));
        Assert.Null(WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(version, "quote_life_landing_unknown"));
    }
}
