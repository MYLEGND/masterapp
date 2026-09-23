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
    public void DraftRoundTripRetainsBindingsOnElementsAndExtraComponents()
    {
        var binding = new WebsiteSignalBinding { EventName = "LeadFormStart", Trigger = "click", DeliveryMode = "analytics" };
        var input = new WebsiteContentDocument
        {
            Pages = new() { ["/contact"] = new() {
                Elements = new() { ["contact-button"] = new() { Signals = [binding] } },
                Extras = [new() { Id = "extra-button", SectionId = "contact", Type = "button", Signals = [binding] }]
            } }
        };
        var clean = WebsiteContentSanitizer.Sanitize(input);
        Assert.Equal(binding.Id, clean.Pages["/contact"].Elements["contact-button"].Signals.Single().Id);
        Assert.Equal("analytics", clean.Pages["/contact"].Extras.Single().Signals.Single().DeliveryMode);
    }

    [Fact]
    public void EditorOptionsUseCentralEventPermissions()
    {
        foreach (var option in WebsiteSignalBindingPolicy.Options)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(option.Name, out var definition));
            Assert.Equal(definition.AllowBrowserPixel || definition.AllowServerForward, option.MetaEligible);
            Assert.Equal(MetaSignalEventCatalog.IsServerAuthorityEvent(option.Name), option.RequiresServerOutcome);
        }
    }
}
