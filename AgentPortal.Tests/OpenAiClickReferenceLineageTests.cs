using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Crm;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiClickReferenceLineageTests
{
    [Fact]
    public void OpprefClassifiesAsPaidWithoutBecomingMetaAttributed()
    {
        var classified = TrafficAttribution.Classify(
            null, null, null, null,
            oppref: "op_click_123");

        Assert.Equal(TrafficType.PaidAds, classified);
        Assert.False(TrafficAttribution.IsMetaAttributedPaid(null, null, null, null));
    }

    [Fact]
    public void ClickReferenceSanitizerRejectsControlAndOversizedValues()
    {
        Assert.Equal("opaque-ref", OpenAiClickReference.Normalize("  opaque-ref  "));
        Assert.Null(OpenAiClickReference.Normalize("bad\nref"));
        Assert.Null(OpenAiClickReference.Normalize(new string('x', OpenAiClickReference.MaxLength + 1)));
    }

    [Fact]
    public async Task ResolverCarriesWebsiteLeadThroughCrmAndConvertedClientToRevenue()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var publicId = Guid.NewGuid();
        var row = new WebsiteLead
        {
            LeadId = publicId,
            FirstName = "A",
            Email = "a@example.com",
            Oppref = "opp_source",
            CreatedUtc = DateTime.UtcNow
        };
        db.WebsiteLeads.Add(row);
        await db.SaveChangesAsync();

        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "workstation-1",
            AgentUserId = "agent-1",
            Bucket = "Life",
            FirstName = "A",
            LastName = "",
            Email = "a@example.com",
            Phone = "",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadRowId = row.Id,
            WebsiteLeadPublicId = publicId,
            WorkstationLeadId = "workstation-1",
            AgentUserId = "agent-1",
            Bucket = "Life",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow,
            Oppref = "opp_source"
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-1",
            FirstName = "A",
            LastName = "Client",
            Email = "a@example.com",
            CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                SourceWorkstationLeadId = "workstation-1",
                RecordType = "Client",
                PipelineStage = "Client"
            })
        });
        await db.SaveChangesAsync();

        var resolver = new OpenAiAttributionLineageResolver(db);
        Assert.Equal("opp_source", await resolver.ResolveForWebsiteLeadAsync(publicId));
        Assert.Equal("opp_source", await resolver.ResolveForWorkstationLeadAsync("workstation-1"));
        Assert.Equal("opp_source", await resolver.ResolveForClientAsync("client-1"));
        Assert.Equal("opp_source", await resolver.ResolveForProductionAsync(ProductionSide.Client, null, "client-1"));
    }

    [Fact]
    public void EntityModelPersistsOpprefAcrossCanonicalJourney()
    {
        using var db = ControllerTestHelpers.BuildDb();
        foreach (var type in new[]
        {
            typeof(AnalyticsEvent),
            typeof(WebsiteLead),
            typeof(WebsiteLeadIntakeLink),
            typeof(LeadAppointment),
            typeof(CommerceOrder),
            typeof(ProductionRecord)
        })
        {
            var property = db.Model.FindEntityType(type)?.FindProperty("Oppref");
            Assert.NotNull(property);
            Assert.Equal(OpenAiClickReference.MaxLength, property!.GetMaxLength());
        }
    }

    [Fact]
    public void BrowserRuntimeSeparatesCurrentOpprefFromFirstTouchAndFormsCarryIt()
    {
        var root = FindRoot();
        var tracking = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "tracking.js"));
        var inquiry = File.ReadAllText(Path.Combine(root, "Legend-Design", "legend-public-inquiry.js"));
        var commerce = File.ReadAllText(Path.Combine(root, "ParfaitApp", "Views", "Shared", "_ParfaitCommerceTracking.cshtml"));

        Assert.Contains("params.get('oppref')", tracking, StringComparison.Ordinal);
        Assert.DoesNotContain("lockedOppref", tracking, StringComparison.Ordinal);
        Assert.Contains("hasAttribution(queryAttribution) ? queryAttribution", tracking, StringComparison.Ordinal);
        Assert.Contains("legend_attr_first_touch:${storageScope}", tracking, StringComparison.Ordinal);
        Assert.Contains("oppref: attribution.oppref", inquiry, StringComparison.Ordinal);
        Assert.Contains("obref: cookie('__obref')", inquiry, StringComparison.Ordinal);
        Assert.Contains("readFirstPartyCookie('__obref')", tracking, StringComparison.Ordinal);
        Assert.Contains("oppref:'oppref'", commerce, StringComparison.Ordinal);
        Assert.Contains("scope:storeScope,sessionId", commerce, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedServerContextPreservesCampaignAndOpenAiClickLineage()
    {
        var context = UnifiedEventContextBuilder.Build(
            httpContext: null,
            eventName: "Lead",
            eventUtc: DateTime.UtcNow,
            utmSource: "chatgpt",
            utmMedium: "paid",
            utmCampaign: "legend_general_life",
            utmId: "campaign-1",
            utmTerm: "ad-group-1",
            utmContent: "ad-1",
            oppref: "opp-click-1",
            obref: "browser-ref-1",
            host: "protect.mylegnd.com",
            isServerAuthority: true);

        var row = UnifiedEventMapper.ToAnalytics(context);

        Assert.Equal("campaign-1", row.UtmId);
        Assert.Equal("ad-group-1", row.UtmTerm);
        Assert.Equal("ad-1", row.UtmContent);
        Assert.Equal("opp-click-1", row.Oppref);
        Assert.Equal("browser-ref-1", CanonicalAdvertisingEventProjection.ReadString(row.MetadataJson, "obref"));
    }

    [Fact]
    public void ProtectQuoteServerOutcomesCarryPersistedOpprefAndDynamicUtmLineage()
    {
        var root = FindRoot();
        foreach (var relative in new[]
        {
            Path.Combine("Protect-Website", "Controllers", "LifeQuoteController.cs"),
            Path.Combine("Protect-Website", "Controllers", "HomeQuoteController.cs"),
            Path.Combine("Protect-Website", "Controllers", "AutoQuoteController.cs"),
            Path.Combine("Protect-Website", "Controllers", "CommercialQuoteController.cs"),
            Path.Combine("Protect-Website", "Controllers", "DisabilityQuoteController.cs"),
            Path.Combine("Protect-Website", "Controllers", "DentalVisionHearingQuoteController.cs")
        })
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains("utmTerm: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, \"UtmTerm\")", source, StringComparison.Ordinal);
            Assert.Contains("utmContent: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, \"UtmContent\")", source, StringComparison.Ordinal);
            Assert.Contains("oppref: lead.Oppref", source, StringComparison.Ordinal);
            Assert.Contains("UnifiedEventContextBuilder.Build(", source, StringComparison.Ordinal);
            Assert.Contains("httpContext: HttpContext", source, StringComparison.Ordinal);
        }

        var builder = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "UnifiedEventContextBuilder.cs"));
        Assert.Contains("MetaLeadTrackingWorkflow.ResolveCookieValue(request, \"__obref\")", builder, StringComparison.Ordinal);
        Assert.Contains("CanUseOpenAiBrowserReference(request)", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void CommerceAndPublicTrackingPreserveOpenAiBrowserReferenceWithoutAParallelStore()
    {
        var root = FindRoot();
        var commerce = File.ReadAllText(Path.Combine(root, "Infrastructure", "Commerce", "CommerceSignalService.cs"));
        var attribution = File.ReadAllText(Path.Combine(root, "Infrastructure", "Commerce", "CommerceSignalAttribution.cs"));
        var inquiry = File.ReadAllText(Path.Combine(root, "Infrastructure", "Leads", "WebsiteInquiryAuthority.cs"));
        var mapper = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "UnifiedEventMapper.cs"));

        Assert.Contains("obref = OpenAiBrowserReference.Normalize(context.Obref)", commerce, StringComparison.Ordinal);
        Assert.Contains("request.Cookies.TryGetValue(\"__obref\"", attribution, StringComparison.Ordinal);
        Assert.Contains("CanUseOpenAiBrowserReference(request)", attribution, StringComparison.Ordinal);
        Assert.Contains("CanUseOpenAiBrowserReference(Request)", inquiry, StringComparison.Ordinal);
        Assert.Contains("obref = OpenAiBrowserReference.Normalize(ctx.Obref)", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<OpenAiBrowser", File.ReadAllText(Path.Combine(root, "Infrastructure", "Data", "MasterAppDbContext.cs")), StringComparison.Ordinal);
        var risk = File.ReadAllText(Path.Combine(root, "Protect-Website", "Controllers", "RiskAssessmentController.cs"));
        Assert.Contains("CanUseOpenAiBrowserReference(Request)", risk, StringComparison.Ordinal);
        var tracking = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "tracking.js"));
        Assert.Contains("navigator.globalPrivacyControl === true ? null : readFirstPartyCookie('__obref')", tracking, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiBrowserReferenceHonorsGlobalPrivacyControlAtCanonicalBuilder()
    {
        var allowed = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        allowed.Request.Headers.Cookie = "__obref=browser-reference";
        Assert.Equal("browser-reference", UnifiedEventContextBuilder.Build(allowed).Obref);

        var blocked = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        blocked.Request.Headers.Cookie = "__obref=browser-reference";
        blocked.Request.Headers["Sec-GPC"] = "1";
        Assert.Null(UnifiedEventContextBuilder.Build(blocked).Obref);
    }

    [Fact]
    public void OpenAiServerMapperReadsOpprefFromCanonicalServerMetadata()
    {
        var row = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Lead",
            Host = "example.com",
            EventUtc = DateTime.UtcNow,
            MetadataJson = "{\"oppref\":\"opp_server\"}"
        };

        Assert.True(OpenAiMeasurementEventMapper.TryMap(row, out var conversion));
        Assert.Equal("opp_server", conversion.Oppref);
    }

    private static string FindRoot()
    {
        var github = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(github) && File.Exists(Path.Combine(github, "MASTERAPP.sln"))) return github;
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
