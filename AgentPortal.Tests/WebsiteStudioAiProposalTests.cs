using System;
using Infrastructure.WebsiteEditing;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteStudioAiProposalTests
{
    private const string ElementId = "home.h1.title";

    [Fact]
    public void ResponsiveProposal_ChangesOnlySelectedResponsiveStyleAndLayout()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument
        {
            Elements = new(StringComparer.Ordinal)
            {
                [ElementId] = new WebsiteElementOverride
                {
                    Text = "Original",
                    Style = new WebsiteStyleOverride { WidthPercent = 80 }
                }
            }
        };

        var result = WebsiteStudioAiProposalPolicy.Apply(
            source,
            "responsive",
            "Improve mobile layout",
            "/",
            ElementId,
            "home.section.1",
            [
                new WebsiteStudioAiOperation
                {
                    Kind = "set_style",
                    BreakpointKey = "mobile",
                    Style = new WebsiteStyleOverride { WidthPercent = 100, FontScale = 0.85m }
                },
                new WebsiteStudioAiOperation
                {
                    Kind = "set_layout",
                    BreakpointKey = "mobile",
                    Layout = new WebsiteLayoutOverride { Mode = "stack", Direction = "column", GapPx = 16 }
                }
            ]);

        var element = result.ProposedDocument.Pages["/"].Elements[ElementId];
        Assert.Equal("Original", element.Text);
        Assert.Equal(80m, element.Style.WidthPercent);
        Assert.Equal(100m, element.BreakpointStyles["mobile"].WidthPercent);
        Assert.Equal(0.85m, element.BreakpointStyles["mobile"].FontScale);
        Assert.Equal("stack", element.BreakpointLayouts["mobile"].Mode);
        Assert.Equal(16m, element.BreakpointLayouts["mobile"].GapPx);
        Assert.Equal("Original", source.Pages["/"].Elements[ElementId].Text);
        Assert.Empty(source.Pages["/"].Elements[ElementId].BreakpointStyles);
    }

    [Fact]
    public void ResponsiveProposal_RejectsContentWritesAndUnknownBreakpoints()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument
        {
            Elements = new(StringComparer.Ordinal)
            {
                [ElementId] = new WebsiteElementOverride { Text = "Original" }
            }
        };

        Assert.Throws<ArgumentException>(() => WebsiteStudioAiProposalPolicy.Apply(
            source, "responsive", "No", "/", ElementId, "home.section.1",
            [new WebsiteStudioAiOperation { Kind = "set_text", Text = "Changed" }]));

        Assert.Throws<ArgumentException>(() => WebsiteStudioAiProposalPolicy.Apply(
            source, "responsive", "No", "/", ElementId, "home.section.1",
            [new WebsiteStudioAiOperation
            {
                Kind = "set_style",
                BreakpointKey = "invented",
                Style = new WebsiteStyleOverride { WidthPercent = 10 }
            }]));
    }

    [Fact]
    public void CreateProposal_AddsTypedBlocksOnlyInsideCurrentPageAndSelectedSection()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument
        {
            Elements = new(StringComparer.Ordinal)
            {
                [ElementId] = new WebsiteElementOverride { Text = "Original" }
            }
        };
        source.Pages["/about"] = new WebsitePageDocument
        {
            Elements = new(StringComparer.Ordinal)
            {
                ["about.h1"] = new WebsiteElementOverride { Text = "Untouched" }
            }
        };

        var result = WebsiteStudioAiProposalPolicy.Apply(
            source,
            "create",
            "Create a concise CTA",
            "/",
            ElementId,
            "home.section.1",
            [
                new WebsiteStudioAiOperation { Kind = "set_text", Text = "Improved headline" },
                new WebsiteStudioAiOperation { Kind = "add_text", Text = "Focused supporting copy." },
                new WebsiteStudioAiOperation
                {
                    Kind = "add_button",
                    Text = "Book now",
                    Href = "https://example.test/book"
                },
                new WebsiteStudioAiOperation
                {
                    Kind = "suggest_image",
                    ImagePrompt = "Professional trainer coaching a client in natural light"
                }
            ]);

        var page = result.ProposedDocument.Pages["/"];
        Assert.Equal("Improved headline", page.Elements[ElementId].Text);
        Assert.Contains(page.Extras, extra =>
            extra.Type == "text" &&
            extra.SectionId == "home.section.1" &&
            extra.Text == "Focused supporting copy.");
        Assert.Contains(page.Extras, extra =>
            extra.Type == "button" &&
            extra.SectionId == "home.section.1" &&
            extra.Text == "Book now" &&
            extra.Href == "https://example.test/book");
        Assert.Equal("Untouched", result.ProposedDocument.Pages["/about"].Elements["about.h1"].Text);
        Assert.Contains(result.Operations, operation =>
            operation.Kind == "suggest_image" &&
            operation.ImagePrompt!.Contains("trainer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(page.Extras, extra => extra.Type == "image");
    }

    [Fact]
    public void Proposal_DoesNotPersistOrPublishByItself()
    {
        var source = new WebsiteContentDocument();
        source.Pages["/"] = new WebsitePageDocument();

        var result = WebsiteStudioAiProposalPolicy.Apply(
            source,
            "create",
            "Draft only",
            "/",
            null,
            "home.section.1",
            [new WebsiteStudioAiOperation { Kind = "add_text", Text = "Draft proposal" }]);

        Assert.Empty(source.Pages["/"].Extras);
        Assert.Single(result.ProposedDocument.Pages["/"].Extras);
    }
}
