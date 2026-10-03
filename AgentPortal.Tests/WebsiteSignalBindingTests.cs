using System;
using System.Collections.Generic;
using System.Linq;
using Infrastructure.WebsiteEditing;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteSignalBindingTests
{
    [Fact]
    public void ClickCannotClaimPurchaseOrLead()
    {
        foreach (var name in new[] { "Lead", "Purchase", "AppointmentBooked" })
            Assert.Throws<ArgumentException>(() => WebsiteSignalBindingPolicy.Validate([
                new() { EventName = name, Trigger = "click", DeliveryMode = "meta" }]));
    }

    [Fact]
    public void SensitiveFieldMappingIsRejected()
    {
        Assert.Throws<ArgumentException>(() => WebsiteSignalBindingPolicy.Validate([
            new() { EventName = "Lead", Trigger = "submission_saved", MatchingFields = ["medicalHistory"] }]));
    }

    [Fact]
    public void ClickCannotCollectContactValues()
    {
        Assert.Throws<ArgumentException>(() => WebsiteSignalBindingPolicy.Validate([
            new() { EventName = "LeadFormStart", Trigger = "click", MatchingFields = ["email"] }]));
    }

    [Fact]
    public void DraftRoundTripRetainsBindingsOnCanonicalCompositionNodes()
    {
        var first = new WebsiteSignalBinding { EventName = "LeadFormStart", Trigger = "click", DeliveryMode = "analytics" };
        var second = new WebsiteSignalBinding { EventName = "LeadFormStart", Trigger = "click", DeliveryMode = "analytics" };
        var input = new WebsiteContentDocument
        {
            Pages = new()
            {
                ["/contact"] = new()
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact-button",
                            Type = "cta",
                            Tag = "a",
                            Text = "Contact",
                            Href = "/contact",
                            Signals = [first]
                        },
                        new WebsiteCompositionNode
                        {
                            Id = "secondary-button",
                            Type = "cta",
                            Tag = "a",
                            Text = "Contact again",
                            Href = "/contact",
                            Signals = [second]
                        }
                    ]
                }
            }
        };

        var clean = WebsiteContentSanitizer.Sanitize(input);
        var nodes = clean.Pages["/contact"].Composition;

        Assert.Equal(first.Id, nodes.Single(node => node.Id == "contact-button").Signals.Single().Id);
        Assert.Equal("analytics", nodes.Single(node => node.Id == "secondary-button").Signals.Single().DeliveryMode);
        Assert.All(nodes, node => Assert.NotEmpty(node.Signals));
    }

    [Fact]
    public void EditorOptionsUseCentralEventPermissions()
    {
        foreach (var option in WebsiteSignalBindingPolicy.Options)
        {
            Assert.True(AnalyticsEventCatalog.TryGetBehavior(option.Name, out var behavior));
            Assert.Equal(behavior.Key, option.ActionKey);
            Assert.Equal(behavior.RequiresServerAuthority, option.RequiresServerOutcome);
            Assert.Equal(behavior.EditorTriggers, option.Triggers);
            if (option.RequiresServerOutcome) Assert.Empty(option.Triggers);
        }
    }
}
