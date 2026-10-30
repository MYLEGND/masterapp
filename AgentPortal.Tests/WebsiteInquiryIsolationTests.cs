using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Infrastructure.Security;
using System.Text;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using ParfaitApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProtectWebsite.Controllers;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

// In-process controller/EF coverage; does not substitute for SQL uniqueness or live CORS proof.
public sealed class WebsiteInquiryIsolationTests
{
    [Fact]
    public async Task AdversarialReplayStormPreservesOneInquiryCrmLeadConversionAndScopedReport()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var request = f.Request();
        for (var attempt = 0; attempt < 500; attempt++)
        {
            Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
            Assert.IsType<ConflictObjectResult>(await f.Controller.Submit(request with { Message = "forged-" + attempt }, CancellationToken.None));
        }
        Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Single(await f.Db.WebsiteLeadIntakeLinks.ToListAsync());
        var source = Assert.Single(await f.Db.AnalyticsEvents.Where(e => e.EventType == "website_lead_submitted").ToListAsync());
        Assert.True(CanonicalAdvertisingEventProjection.CanProjectServer(source));
        Assert.Equal(f.BusinessId, source.CommerceBusinessId);
        var reporting = new AnalyticsQueryService(f.Db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddHours(-1), ToUtc = DateTime.UtcNow.AddMinutes(1), QualityMode = TrafficQualityMode.AllTraffic };
        Assert.Equal(1, (await reporting.GetSummaryAsync(range, ScopeContext.ForBusiness(f.BusinessId))).VerifiedLeads);
        Assert.Equal(1, (await reporting.GetConversionsAsync(range, ScopeContext.ForBusiness(f.BusinessId))).TotalConversions);
        Assert.Equal(1, (await reporting.GetLeadsAsync(range, ScopeContext.ForBusiness(f.BusinessId))).Total);
        Assert.Equal(0, (await reporting.GetSummaryAsync(range, ScopeContext.ForBusiness(Guid.NewGuid()))).VerifiedLeads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://business.example")]
    [InlineData("https://business.example/path")]
    [InlineData("https://business.example:8443")]
    public async Task MalformedOriginCannotCreateInquiry(string origin)
    {
        using var f = new Fixture(origin);
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task UnknownDomainCannotRouteToFounderOrAnotherBusiness()
    {
        using var f = new Fixture("https://unknown.example");
        await f.SeedPublishedAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task VerifiedDomainWithoutPublishedVersionCannotReceiveInquiry()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var state = await f.Db.Set<WebsiteContentState>().SingleAsync();
        state.PublishedVersionId = null;
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task SubmissionStaysWithVerifiedBusinessAndRetriesDoNotDuplicate()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var request = f.Request();
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        var row = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Equal(f.BusinessId, row.CommerceBusinessId);
        Assert.Equal(f.VersionId, row.PublishedVersionId);
        Assert.Equal("New", row.Status);
        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(f.BusinessId, lead.CommerceBusinessId);
        Assert.Equal(f.VersionId, lead.WebsiteContentVersionId);
        Assert.Equal("business_contact", lead.WebsiteBindingId);
        Assert.Equal("business-session", lead.SessionId);
        Assert.Equal("Visitor", lead.FirstName);
        Assert.Equal("Example", lead.LastName);
        Assert.Equal("(602) 555-0199", lead.Phone);
        Assert.Equal("visitor@example.org", lead.Email);
        Assert.Null(lead.AgentTrackingProfileId);
        var businessCrm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal(f.BusinessId, businessCrm.CommerceBusinessId);
        Assert.Equal("", businessCrm.AgentUserId);
        Assert.Equal("Lead", businessCrm.CrmStatus);
        var businessIntake = Assert.Single(await f.Db.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.Equal(lead.LeadId, businessIntake.WebsiteLeadPublicId);
        Assert.Equal(businessCrm.LeadId, businessIntake.WorkstationLeadId);
        Assert.Equal(f.BusinessId, businessIntake.CommerceBusinessId);
        var analytics = Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(f.BusinessId, analytics.CommerceBusinessId);
        Assert.Equal(f.VersionId, analytics.WebsiteContentVersionId);
        Assert.Equal("business_contact", analytics.WebsiteBindingId);
        Assert.Null(analytics.AgentTrackingProfileId);
        Assert.IsType<ConflictObjectResult>(await f.Controller.Submit(request with { Message = "Different request" }, CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await f.Controller.Submit(request with { Phone = "(602) 555-0100" }, CancellationToken.None));
    }

    [Fact]
    public async Task BusinessInquiryAcknowledgesBeforeDelivery_AndCanonicalV3LeadRemainsServerAuthorityEligible()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();

        var business = await f.Db.CommerceBusinesses.SingleAsync(x => x.Id == f.BusinessId);
        business.OwnerEmail = "owner@example.org";
        var profile = new ClientProfile
        {
            ClientUserId = Guid.NewGuid().ToString(),
            Email = "owner@example.org"
        };
        var member = new CommerceBusinessMember
        {
            CommerceBusinessId = f.BusinessId,
            ClientProfileId = profile.Id,
            Email = profile.Email,
            NormalizedEmail = profile.Email.ToUpperInvariant(),
            DisplayName = "Owner"
        };
        f.Db.AddRange(profile, member);

        var document = new WebsiteContentDocument
        {
            Pages =
            {
                ["/contact"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact-form",
                            Type = "form",
                            Tag = "form",
                            SystemKey = "canonical_inquiry",
                            Title = "Contact us",
                            Text = "Send inquiry"
                        }
                    ]
                }
            }
        };
        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSystemTemplateAuthority.Apply(WebsiteEditorSiteKeys.Business, document);
        var version = await f.Db.Set<WebsiteContentVersion>().SingleAsync(x => x.Id == f.VersionId);
        version.DocumentJson = System.Text.Json.JsonSerializer.Serialize(
            document,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        version.CompiledPagesJson = "{\"pages\":{\"/contact\":{\"html\":\"published\"}}}";
        await f.Db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with { SourceFormElementId = "contact-form" },
            CancellationToken.None));
        var resultJson = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"notificationSent\":false", resultJson, StringComparison.OrdinalIgnoreCase);

        var inquiry = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Equal("Pending", inquiry.NotificationStatus);
        Assert.Null(inquiry.NotificationSentUtc);
        Assert.Equal(0, inquiry.NotificationAttempts);
        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        Assert.Equal("Sent", inquiry.NotificationStatus);
        Assert.NotNull(inquiry.NotificationSentUtc);

        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "owner@example.org",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal("contact-form", analytics.WebsiteBindingId);
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(
            analytics.MetadataJson,
            "metaServerAuthorityEligible") == true);
        Assert.DoesNotContain("WebsiteSignalBindingId", analytics.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FounderLegendOriginUsesTheSameCanonicalInquiryLeadWithoutCommerceDuplication()
    {
        using var f = new Fixture("https://www.mylegnd.com");
        await f.SeedLegendPublishedAsync();
        var request = f.Request() with
        {
            SourceActionKey = "legend_contact",
            SessionId = "legend-session",
            VisitorId = "legend-visitor"
        };

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"accepted\":true", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Null(lead.CommerceBusinessId);
        Assert.Null(lead.AgentTrackingProfileId);
        Assert.Equal(f.VersionId, lead.WebsiteContentVersionId);
        Assert.Equal("LegendInquiry", lead.InterestType);
        Assert.Equal("legend_contact", lead.WebsiteBindingId);
        Assert.Equal("www.mylegnd.com", lead.Host);
        Assert.Equal("Please contact me.", lead.Notes);
        Assert.True(lead.TermsAccepted);
        Assert.False(lead.MarketingEmailConsent);
        Assert.False(lead.CallTextConsent);
        Assert.Contains("\"ReportingOwner\":\"founder\"", lead.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);

        var founderCrm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal("founder-user", founderCrm.AgentUserId);
        Assert.Null(founderCrm.CommerceBusinessId);
        Assert.Equal("Lead", founderCrm.CrmStatus);
        var founderIntake = Assert.Single(await f.Db.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.Equal(lead.LeadId, founderIntake.WebsiteLeadPublicId);
        Assert.Equal(founderCrm.LeadId, founderIntake.WorkstationLeadId);
        Assert.Null(founderIntake.CommerceBusinessId);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Null(analytics.CommerceBusinessId);
        Assert.Equal(f.VersionId, analytics.WebsiteContentVersionId);
        Assert.Contains("\"siteKey\":\"legend\"", analytics.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"reportingOwner\":\"founder\"", analytics.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);

        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "founder@example.org",
            It.Is<string>(subject => subject.Contains("LEGEND", StringComparison.OrdinalIgnoreCase)),
            It.Is<string>(html => html.Contains("#0f172a", StringComparison.OrdinalIgnoreCase) &&
                                  html.Contains("Visitor Example", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FounderProtectContactUsesSameCanonicalLeadCrmAnalyticsEmailAndOwnerLineage()
    {
        using var f = new Fixture("https://protect.mylegnd.com");
        var tracking = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "founder-user",
            AgentUpn = "founder@example.org",
            Slug = "founder",
            DisplayName = "Founder",
            Status = "active",
            UpdatedUtc = DateTime.UtcNow
        };
        f.Db.Add(tracking);
        f.Db.Add(new AgentProfile
        {
            AgentUserId = tracking.AgentUserId,
            AgentUpn = "founder@example.org",
            NormalizedEmail = "founder@example.org",
            FullName = "Founder",
            IsActive = true,
            UpdatedUtc = DateTime.UtcNow
        });
        var state = new WebsiteContentState
        {
            OwnerKey = tracking.AgentUserId,
            SiteKey = WebsiteEditorSiteKeys.Protect,
            PublishedVersionId = f.VersionId
        };
        f.Db.Add(state);
        f.Db.Add(new WebsiteContentVersion
        {
            Id = f.VersionId,
            StateId = state.Id,
            CompiledPagesJson = "{\"pages\":{\"/contact\":{\"html\":\"published\"}}}"
        });
        await f.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(f.Db);
        services.AddSingleton(new AgentTrackingResolver(f.Db, NullLogger<AgentTrackingResolver>.Instance));
        f.Controller.HttpContext.RequestServices = services.BuildServiceProvider();

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with
            {
                SourcePath = "/contact",
                SourceActionKey = "protect_founder_contact",
                SessionId = "protect-founder-session",
                VisitorId = "protect-founder-visitor"
            },
            CancellationToken.None));
        Assert.Contains("\"accepted\":true", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(tracking.Id, lead.AgentTrackingProfileId);
        Assert.Null(lead.CommerceBusinessId);
        Assert.Equal("ProtectionInquiry", lead.InterestType);
        Assert.Contains("\"ReportingOwner\":\"founder\"", lead.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);

        var crm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal("founder-user", crm.AgentUserId);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(tracking.Id, analytics.AgentTrackingProfileId);
        Assert.Contains("\"siteKey\":\"protect\"", analytics.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"reportingOwner\":\"founder\"", analytics.MetadataJson ?? "", StringComparison.OrdinalIgnoreCase);

        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "founder@example.org",
            It.Is<string>(subject => subject.Contains("Protection", StringComparison.OrdinalIgnoreCase)),
            It.Is<string>(html => html.Contains("#0f172a", StringComparison.OrdinalIgnoreCase) &&
                                  html.Contains("Visitor Example", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProtectAgentContactUsesCanonicalInquiryLeadCrmAnalyticsAndPrimaryEmail()
    {
        using var f = new Fixture("https://protect.mylegnd.com");
        var tracking = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "advisor-user",
            AgentUpn = "legacy-advisor@example.org",
            Slug = "advisor",
            DisplayName = "Advisor",
            Status = "active",
            UpdatedUtc = DateTime.UtcNow
        };
        f.Db.Add(tracking);
        f.Db.Add(new AgentProfile
        {
            AgentUserId = tracking.AgentUserId,
            AgentUpn = "Primary.Advisor@Example.org",
            NormalizedEmail = "primary.advisor@example.org",
            IsActive = true,
            UpdatedUtc = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(f.Db);
        services.AddSingleton(new AgentTrackingResolver(f.Db, NullLogger<AgentTrackingResolver>.Instance));
        f.Controller.HttpContext.RequestServices = services.BuildServiceProvider();

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with
            {
                SourcePath = "/a/advisor/Contact",
                SourceActionKey = "protect_contact",
                SessionId = "protect-session",
                VisitorId = "protect-visitor"
            },
            CancellationToken.None));
        Assert.Contains("\"accepted\":true", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(tracking.Id, lead.AgentTrackingProfileId);
        Assert.Null(lead.CommerceBusinessId);
        Assert.Equal("ProtectionInquiry", lead.InterestType);
        Assert.Equal("/a/advisor/Contact", lead.SourcePageKey);

        var crm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal(tracking.AgentUserId, crm.AgentUserId);
        Assert.Null(crm.CommerceBusinessId);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(tracking.Id, analytics.AgentTrackingProfileId);
        Assert.Null(analytics.CommerceBusinessId);

        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "primary.advisor@example.org",
            It.Is<string>(subject => subject.Contains("Protection", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("/Contact", false)]
    [InlineData("/store/checkout", false)]
    [InlineData("/store/checkout", true)]
    public async Task ParfaitInquiryUsesCanonicalBusinessInquiryLeadCrmAnalyticsAndPrimaryEmail(string sourcePath, bool publishedDomain)
    {
        using var f = new Fixture("https://shopparfait.com");
        var businessId = Guid.NewGuid();
        f.Db.Add(new CommerceBusiness
        {
            Id = businessId,
            Key = ParfaitBusinessScopeService.ParfaitBusinessKey,
            DisplayName = "Parfait",
            LegalName = "MyLegnd LLC",
            BusinessType = "Apparel / Ecommerce",
            PrimaryDomain = "shopparfait.com",
            Status = "Active",
            IsActive = true,
            OwnerEmail = "parfait-primary@example.org"
        });
        f.Db.Add(new CommerceBusinessStorefrontSettings
        {
            CommerceBusinessId = businessId,
            WorkspacePreferencesJson = new Shared.Crm.BusinessWorkspacePreferences().Write()
        });
        if (publishedDomain)
        {
            f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = businessId, Hostname = "shopparfait.com", Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
            var state = new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(businessId), SiteKey = WebsiteEditorSiteKeys.Business, PublishedVersionId = Guid.Parse("8c369adc-4f12-4f68-9ff0-a48898972b71") };
            f.Db.Add(state);
            f.Db.Add(new WebsiteContentVersion { Id = state.PublishedVersionId.Value, StateId = state.Id, CompiledPagesJson = "{\"pages\":{\"/contact\":{\"html\":\"published\"}}}" });
        }
        await f.Db.SaveChangesAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commerce:PublicBaseUrl"] = "https://shopparfait.com"
        }).Build();
        var domains = new WebsiteDomainService(f.Db, Mock.Of<IHttpClientFactory>(), config);
        var stores = new CommerceStoreContextService(
            f.Db,
            new CommerceBusinessScopeResolver(f.Db),
            new ParfaitBusinessScopeService(f.Db),
            domains,
            config);
        var services = new ServiceCollection();
        services.AddSingleton(f.Db);
        services.AddSingleton(stores);
        f.Controller.HttpContext.RequestServices = services.BuildServiceProvider();

        var request = f.Request() with
        {
            SourcePath = sourcePath,
            SourceActionKey = "parfait_inquiry",
            SessionId = "parfait-session",
            VisitorId = "parfait-visitor"
        };
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(request with { Consent = false }, CancellationToken.None));
        foreach (var invalidPath in new[] { "/store/checkout/pay", "/store/checkout/other", "/store/s/other/checkout", "/store/checkout?email=private" })
        {
            var denied = await f.Controller.Submit(request with { SourcePath = invalidPath }, CancellationToken.None);
            Assert.True(denied is BadRequestObjectResult or NotFoundObjectResult);
        }
        Assert.Empty(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Empty(await f.Db.WorkstationLeadProfiles.ToListAsync());
        if (publishedDomain)
        {
            await f.SeedPublishedAsync();
            var mapped = await f.Db.Set<WebsiteDomainBinding>().SingleAsync(x => x.Hostname == "shopparfait.com");
            mapped.CommerceBusinessId = f.BusinessId;
            var otherVersion = await f.Db.Set<WebsiteContentVersion>().SingleAsync(x => x.Id == f.VersionId);
            otherVersion.CompiledPagesJson = "{\"pages\":{\"/store/checkout\":{\"html\":\"published\"}}}";
            await f.Db.SaveChangesAsync();
            Assert.IsType<NotFoundObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
            Assert.Empty(await f.Db.WebsiteLeads.ToListAsync());
            Assert.Empty(await f.Db.WorkstationLeadProfiles.ToListAsync());
            Assert.Empty(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
            mapped.CommerceBusinessId = businessId;
            await f.Db.SaveChangesAsync();
        }
        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        Assert.Contains("\"accepted\":true", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(publishedDomain ? 1 : 0, await f.Db.Set<CommerceWebsiteInquiry>().CountAsync());

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(businessId, lead.CommerceBusinessId);
        Assert.Null(lead.AgentTrackingProfileId);
        Assert.Equal("BusinessInquiry", lead.InterestType);
        Assert.Contains("\"IsCommerceApp\":true", lead.MetadataJson ?? "", StringComparison.Ordinal);
        Assert.False(lead.MarketingEmailConsent);
        Assert.False(lead.CallTextConsent);

        var crm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal(businessId, crm.CommerceBusinessId);
        Assert.Equal(string.Empty, crm.AgentUserId);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(businessId, analytics.CommerceBusinessId);
        Assert.Null(analytics.AgentTrackingProfileId);

        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "parfait-primary@example.org",
            It.Is<string>(subject => publishedDomain ? subject == "New website inquiry" : subject.Contains("Parfait", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeniedMeasurementConsentDropsProviderMatchingIdentifiersFromInquiryTruth()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var request = f.Request() with
        {
            Obref = "browser-ref",
            Fbp = "fb-browser",
            Fbc = "fb-click",
            MeasurementConsent = "denied"
        };

        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, CancellationToken.None));
        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Null(lead.Fbp);
        Assert.Null(lead.Fbc);
        Assert.Null(CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, "Obref"));

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Null(CanonicalAdvertisingEventProjection.ReadString(analytics.MetadataJson, "obref"));
        Assert.False(CanonicalAdvertisingEventProjection.ReadBoolean(
            analytics.MetadataJson, "measurementConsentAllowed") == true);
        Assert.Equal("denied", CanonicalAdvertisingEventProjection.ReadString(
            analytics.MetadataJson, "measurementConsentState"));
    }

    [Fact]
    public async Task GrantedMeasurementConsentIsPreservedOnCanonicalBusinessInquiryConversion()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();

        Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with { MeasurementConsent = "granted" },
            CancellationToken.None));

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(f.BusinessId, analytics.CommerceBusinessId);
        Assert.True(CanonicalAdvertisingEventProjection.ReadBoolean(
            analytics.MetadataJson, "measurementConsentAllowed") == true);
        Assert.Equal("granted", CanonicalAdvertisingEventProjection.ReadString(
            analytics.MetadataJson, "measurementConsentState"));
        Assert.True(CanonicalAdvertisingEventProjection.CanProjectServer(analytics));
    }

    [Fact]
    public async Task ConsentRequiredAndSourceCannotContainPrivateQuery()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { Consent = false }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { Phone = "123" }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { FirstName = "" }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(f.Request() with { SourcePath = "/?ticket=private" }, CancellationToken.None));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Fact]
    public async Task ManagementProjectsPhoneAndSplitNameFromCanonicalWebsiteLead()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
        f.Db.Add(profile);
        f.Db.Add(new CommerceBusinessMember { CommerceBusinessId = f.BusinessId, ClientProfileId = profile.Id });
        await f.Db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await f.Controller.Submit(f.Request(), CancellationToken.None));
        var result = Assert.IsType<OkObjectResult>(await f.Controller.Manage(f.Ticket(profile), CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"FirstName\":\"Visitor\"", json);
        Assert.Contains("\"LastName\":\"Example\"", json);
        Assert.Contains("\"Phone\":\"(602) 555-0199\"", json);
        Assert.Contains("\"Email\":\"visitor@example.org\"", json);
    }

    [Fact]
    public async Task MissingManagementAuthorityCannotReadOrMutateInbox()
    {
        using var f = new Fixture();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage("invalid", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Status(new("invalid", Guid.NewGuid(), "Closed"), CancellationToken.None));
    }

    [Fact]
    public async Task ManagementCannotReadOrChangeAnotherBusinessAndRevocationIsImmediate()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), CrmNotes = "{\"recordType\":\"BusinessClient\"}" };
        var member = new CommerceBusinessMember { CommerceBusinessId = f.BusinessId, ClientProfileId = profile.Id };
        f.Db.Add(profile); f.Db.Add(member);
        var own = new CommerceWebsiteInquiry { CommerceBusinessId = f.BusinessId, PublishedVersionId = f.VersionId, SubmissionId = Guid.NewGuid(), Message = "Owned" };
        var other = new CommerceWebsiteInquiry { CommerceBusinessId = Guid.NewGuid(), PublishedVersionId = f.VersionId, SubmissionId = Guid.NewGuid(), Message = "Private other business" };
        f.Db.Add(own); f.Db.Add(other);
        await f.Db.SaveChangesAsync();
        var ticket = f.Ticket(profile);
        var result = Assert.IsType<OkObjectResult>(await f.Controller.Manage(ticket, CancellationToken.None));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Owned", json);
        Assert.DoesNotContain("Private other business", json);
        Assert.IsType<NotFoundResult>(await f.Controller.Status(new(ticket, other.Id, "Closed"), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await f.Controller.Status(new(ticket, own.Id, "Contacted"), CancellationToken.None));
        member.Status = "Inactive";
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Manage(ticket, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await f.Controller.Status(new(ticket, own.Id, "Closed"), CancellationToken.None));
        Assert.Equal("Contacted", own.Status);
        Assert.Equal("New", other.Status);
    }

    [Fact]
    public async Task BusinessPageViewUsesVerifiedHostAndRejectsCrossBusinessEventReplay()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();
        var version = await f.Db.Set<WebsiteContentVersion>().SingleAsync();
        version.CompiledPagesJson = "{\"pages\":{\"/\":{\"html\":\"test\"}}}";
        await f.Db.SaveChangesAsync();
        var controller = WebsiteTrackingIngestTests.BuildController(f.Db);
        var config = new ConfigurationBuilder().Build();
        var domains = new WebsiteDomainService(f.Db, Mock.Of<IHttpClientFactory>(), config);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(f.Db);
        services.AddSingleton(new PublicWebsiteRuntimeScopeResolver(f.Db, domains, config));
        controller.HttpContext.RequestServices = services.BuildServiceProvider();
        controller.Request.Host = new HostString("business.example");
        controller.Request.Headers.Origin = "https://business.example";
        var request = new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(), SessionId = Guid.NewGuid().ToString("N"),
            VisitorId = Guid.NewGuid().ToString("N"), Path = "/", SiteKey = WebsiteEditorSiteKeys.Business,
            EventType = "page_view"
        };
        Assert.IsType<OkObjectResult>(await controller.Ingest(request, default));
        Assert.IsType<OkObjectResult>(await controller.Ingest(request, default));
        var row = Assert.Single(await f.Db.AnalyticsEvents.ToListAsync());
        Assert.Equal(f.BusinessId, row.CommerceBusinessId);
        Assert.Null(row.AgentTrackingProfileId);
        Assert.Equal(request.VisitorId, row.VisitorId);
        Assert.NotEqual(row.VisitorId, row.SessionId);
        Assert.DoesNotContain("Insurance", row.MetadataJson);
        var originalEventId = row.EventId;
        // Forged site ownership is rejected before event deduplication.
        request.SiteKey = WebsiteEditorSiteKeys.Protect;
        var forgedSite = Assert.IsType<BadRequestObjectResult>(await controller.Ingest(request, default));
        Assert.Equal("published_website_scope_required", JsonSerializer.SerializeToElement(forgedSite.Value).GetProperty("error").GetString());
        var unchanged = Assert.Single(await f.Db.AnalyticsEvents.AsNoTracking().ToListAsync());
        Assert.Equal(f.BusinessId, unchanged.CommerceBusinessId);
        Assert.Equal(originalEventId, unchanged.EventId);

        // A second legitimate published owner must still reject replay of the first owner's event.
        var otherBusinessId = Guid.NewGuid();
        var otherVersionId = Guid.NewGuid();
        f.Db.Add(new CommerceBusiness { Id = otherBusinessId, Key = "other-business", DisplayName = "Other Business" });
        f.Db.Add(new CommerceBusinessStorefrontSettings { CommerceBusinessId = otherBusinessId });
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = otherBusinessId, Hostname = "other-business.example",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        var otherState = new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(otherBusinessId),
            SiteKey = WebsiteEditorSiteKeys.Business, PublishedVersionId = otherVersionId };
        f.Db.Add(otherState);
        f.Db.Add(new WebsiteContentVersion { Id = otherVersionId, StateId = otherState.Id, CompiledPagesJson = version.CompiledPagesJson });
        await f.Db.SaveChangesAsync();
        request.SiteKey = WebsiteEditorSiteKeys.Business;
        controller.Request.Host = new HostString("other-business.example");
        controller.Request.Headers.Origin = "https://other-business.example";
        var replay = Assert.IsType<ConflictObjectResult>(await controller.Ingest(request, default));
        Assert.Equal("event_id_owner_conflict", JsonSerializer.SerializeToElement(replay.Value).GetProperty("error").GetString());
        unchanged = Assert.Single(await f.Db.AnalyticsEvents.AsNoTracking().ToListAsync());
        Assert.Equal(f.BusinessId, unchanged.CommerceBusinessId);
        Assert.Equal(originalEventId, unchanged.EventId);
        controller.Request.Host = new HostString("business.example");
        controller.Request.Headers.Origin = "https://business.example";
        request.ClientEventId = Guid.NewGuid();
        request.Path = "/unpublished";
        Assert.IsType<BadRequestObjectResult>(await controller.Ingest(request, default));
        request.Path = "/";
        controller.Request.Headers.Origin = "https://foreign.example";
        Assert.IsType<BadRequestObjectResult>(await controller.Ingest(request, default));
        controller.Request.Scheme = "https";
        controller.Request.Method = "POST";
        foreach (var invalidOrigin in new[] { "", "https://foreign.example/path", "null" })
        {
            controller.Request.Headers.Origin = invalidOrigin;
            Assert.IsType<BadRequestObjectResult>(await controller.Ingest(request, default));
        }
        Assert.Single(await f.Db.AnalyticsEvents.ToListAsync());
    }


    [Fact]
    public async Task NativeExperience_UsesPublishedSchemaAndIncludesCustomAnswersInOwnerNotification()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();

        var business = await f.Db.CommerceBusinesses.SingleAsync(x => x.Id == f.BusinessId);
        business.OwnerEmail = "owner@example.org";
        var ownerProfile = new ClientProfile
        {
            ClientUserId = Guid.NewGuid().ToString(),
            Email = "owner@example.org"
        };
        f.Db.Add(ownerProfile);
        f.Db.Add(new CommerceBusinessMember
        {
            CommerceBusinessId = f.BusinessId,
            ClientProfileId = ownerProfile.Id,
            Email = ownerProfile.Email,
            NormalizedEmail = ownerProfile.Email.ToUpperInvariant(),
            DisplayName = "Owner"
        });

        var experience = new WebsiteCompositionNode
        {
            Id = "contact.project-estimator",
            Type = "experience",
            Tag = "form",
            Title = "Project estimator",
            Experience = new WebsiteExperienceDefinition
            {
                Kind = "calculator",
                SubmitCapability = WebsiteExperiencePolicy.LeadCaptureCapability,
                Controls =
                [
                    new() { Key = "first_name", Type = "text", Label = "First name", Required = true, ContactRole = "first_name" },
                    new() { Key = "last_name", Type = "text", Label = "Last name", Required = true, ContactRole = "last_name" },
                    new() { Key = "phone", Type = "tel", Label = "Phone", Required = true, ContactRole = "phone" },
                    new() { Key = "email", Type = "email", Label = "Email", Required = true, ContactRole = "email" },
                    new() { Key = "consent", Type = "checkbox", Label = "Share my inquiry", Required = true, ContactRole = "consent" },
                    new()
                    {
                        Key = "project_type", Type = "choice", Label = "Project type", Required = true,
                        Options =
                        [
                            new() { Value = "installation", Label = "New installation" },
                            new() { Value = "repair", Label = "Repair / upgrade" }
                        ]
                    },
                    new() { Key = "project_size", Type = "number", Label = "Project size", Required = true, Min = 100, Max = 10000 },
                    new() { Key = "submit", Type = "button", Label = "Send", Action = new() { Type = "submit" } }
                ],
                Steps =
                [
                    new() { Key = "main", ControlKeys = ["project_type", "project_size", "first_name", "last_name", "phone", "email", "consent", "submit"] }
                ],
                Calculations = new(StringComparer.Ordinal)
                {
                    ["estimate"] = new WebsiteExperienceExpression
                    {
                        Op = "multiply",
                        Values =
                        [
                            new() { Op = "ref", Ref = "project_size" },
                            new() { Op = "value", Value = System.Text.Json.JsonSerializer.SerializeToElement(2m) }
                        ]
                    }
                },
                Results =
                [
                    new()
                    {
                        Key = "estimate",
                        Label = "Preliminary estimate",
                        Format = "currency",
                        Expression = new() { Op = "ref", Ref = "calc.estimate" }
                    }
                ]
            }
        };

        var document = new WebsiteContentDocument
        {
            Pages =
            {
                ["/contact"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact.section",
                            Type = "section",
                            Tag = "section",
                            Children = [experience]
                        }
                    ]
                }
            }
        };
        var version = await f.Db.Set<WebsiteContentVersion>().SingleAsync(x => x.Id == f.VersionId);
        version.DocumentJson = System.Text.Json.JsonSerializer.Serialize(
            WebsiteContentSanitizer.Sanitize(document),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();

        var answers = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["first_name"] = System.Text.Json.JsonSerializer.SerializeToElement("Custom"),
            ["last_name"] = System.Text.Json.JsonSerializer.SerializeToElement("Visitor"),
            ["phone"] = System.Text.Json.JsonSerializer.SerializeToElement("(602) 555-0199"),
            ["email"] = System.Text.Json.JsonSerializer.SerializeToElement("custom@example.org"),
            ["consent"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            ["project_type"] = System.Text.Json.JsonSerializer.SerializeToElement("installation"),
            ["project_size"] = System.Text.Json.JsonSerializer.SerializeToElement(1500)
        };

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with
            {
                FirstName = "",
                LastName = "",
                Phone = "",
                Email = "",
                Message = "",
                Consent = false,
                SourceActionKey = null,
                SourceFormElementId = experience.Id,
                ExperienceId = experience.Id,
                Answers = answers
            },
            CancellationToken.None));
        Assert.Contains("\"accepted\":true", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);

        var inquiry = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Contains("Project type: New installation", inquiry.Message, StringComparison.Ordinal);
        Assert.Contains("Project size: 1500", inquiry.Message, StringComparison.Ordinal);
        Assert.Contains("Preliminary estimate:", inquiry.Message, StringComparison.Ordinal);

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal("Custom", lead.FirstName);
        Assert.Equal("Visitor", lead.LastName);
        Assert.Equal("custom@example.org", lead.Email);
        Assert.Contains("\"ExperienceId\":\"contact.project-estimator\"", lead.MetadataJson ?? "", StringComparison.Ordinal);

        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "owner@example.org",
            It.IsAny<string>(),
            It.Is<string>(html => html.Contains("New installation", StringComparison.Ordinal) &&
                                  html.Contains("Preliminary estimate", StringComparison.Ordinal)),
            It.IsAny<string?>(),
            "custom@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NativeExperience_RejectsAnswersNotDeclaredByPublishedSchema()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();

        var experience = new WebsiteCompositionNode
        {
            Id = "contact.secure-form",
            Type = "experience",
            Tag = "form",
            Experience = new WebsiteExperienceDefinition
            {
                Kind = "form",
                SubmitCapability = WebsiteExperiencePolicy.LeadCaptureCapability,
                Controls =
                [
                    new() { Key = "first_name", Type = "text", Required = true, ContactRole = "first_name" },
                    new() { Key = "last_name", Type = "text", Required = true, ContactRole = "last_name" },
                    new() { Key = "phone", Type = "tel", Required = true, ContactRole = "phone" },
                    new() { Key = "email", Type = "email", Required = true, ContactRole = "email" },
                    new() { Key = "consent", Type = "checkbox", Required = true, ContactRole = "consent" },
                    new() { Key = "submit", Type = "button", Action = new() { Type = "submit" } }
                ]
            }
        };
        var document = new WebsiteContentDocument
        {
            Pages =
            {
                ["/contact"] = new WebsitePageDocument
                {
                    Composition = [new() { Id = "contact.section", Type = "section", Tag = "section", Children = [experience] }]
                }
            }
        };
        var version = await f.Db.Set<WebsiteContentVersion>().SingleAsync(x => x.Id == f.VersionId);
        version.DocumentJson = System.Text.Json.JsonSerializer.Serialize(
            WebsiteContentSanitizer.Sanitize(document),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();

        var answers = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["first_name"] = System.Text.Json.JsonSerializer.SerializeToElement("Secure"),
            ["last_name"] = System.Text.Json.JsonSerializer.SerializeToElement("Visitor"),
            ["phone"] = System.Text.Json.JsonSerializer.SerializeToElement("(602) 555-0199"),
            ["email"] = System.Text.Json.JsonSerializer.SerializeToElement("secure@example.org"),
            ["consent"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            ["forged_server_field"] = System.Text.Json.JsonSerializer.SerializeToElement("Lead")
        };

        var response = await f.Controller.Submit(
            f.Request() with { ExperienceId = experience.Id, Answers = answers },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Empty(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Empty(await f.Db.AnalyticsEvents.ToListAsync());
    }


    [Fact]
    public async Task PublishedExperienceBinding_EnrichesObservedEventWithoutAllowingBrowserRetargeting()
    {
        using var f = new Fixture();
        await f.SeedPublishedAsync();

        var binding = WebsiteSignalBindingPolicy.Validate(
        [
            new WebsiteSignalBinding
            {
                Id = Guid.NewGuid().ToString("N"),
                Trigger = "field_completed",
                EventName = "PhoneFieldCompleted",
                DeliveryMode = "destinations",
                OncePerSession = false
            }
        ]).Single();

        var document = new WebsiteContentDocument
        {
            Pages =
            {
                ["/contact"] = new WebsitePageDocument
                {
                    Composition =
                    [
                        new WebsiteCompositionNode
                        {
                            Id = "contact.phone-experience",
                            Type = "experience",
                            Tag = "form",
                            Experience = new WebsiteExperienceDefinition
                            {
                                Kind = "form",
                                Controls =
                                [
                                    new()
                                    {
                                        Key = "phone",
                                        Type = "tel",
                                        Label = "Phone",
                                        ContactRole = "phone"
                                    }
                                ]
                            },
                            FieldSignals = new(StringComparer.Ordinal)
                            {
                                ["phone"] = [binding]
                            }
                        }
                    ]
                }
            }
        };
        document = WebsiteContentSanitizer.Sanitize(document);
        WebsiteSiteSource.ValidateCanonical(document, WebsiteCallToActionCatalog.Build(WebsiteEditorSiteKeys.Business));

        var version = await f.Db.Set<WebsiteContentVersion>().SingleAsync(x => x.Id == f.VersionId);
        version.DocumentJson = System.Text.Json.JsonSerializer.Serialize(
            document,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();

        var config = new ConfigurationBuilder().Build();
        var domains = new WebsiteDomainService(f.Db, Mock.Of<IHttpClientFactory>(), config);
        var controller = WebsiteTrackingIngestTests.BuildController(f.Db);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(f.Db);
        services.AddSingleton(new PublicWebsiteRuntimeScopeResolver(f.Db, domains, config));
        controller.HttpContext.RequestServices = services.BuildServiceProvider();
        controller.Request.Host = new HostString("business.example");
        controller.Request.Headers.Origin = "https://business.example";

        var elementId = "contact.phone-experience:field:phone";
        var request = new TrackingProxyController.AnalyticsEventRequest
        {
            ClientEventId = Guid.NewGuid(),
            SessionId = Guid.NewGuid().ToString("N"),
            VisitorId = Guid.NewGuid().ToString("N"),
            Path = "/contact",
            SiteKey = WebsiteEditorSiteKeys.Business,
            EventType = "form_field_complete",
            FormKey = "experience:contact.phone-experience",
            FieldName = "phone",
            WebsiteBindingId = binding.Id,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                configuredWebsiteSignal = true,
                configuredSignalBindings = new[]
                {
                    new
                    {
                        id = binding.Id,
                        elementId,
                        eventName = "Purchase",
                        actionKey = "forged_purchase",
                        deliveryMode = "destinations"
                    }
                }
            })
        };

        Assert.IsType<OkObjectResult>(await controller.Ingest(request, CancellationToken.None));
        var row = Assert.Single(await f.Db.AnalyticsEvents.ToListAsync());
        Assert.Equal("form_field_complete", row.EventType);
        Assert.Equal(binding.Id, row.WebsiteBindingId);
        Assert.Contains("\"ActionKey\":\"phone_field_completed\"", row.MetadataJson ?? "", StringComparison.Ordinal);
        Assert.Equal("phone", row.FieldName);
        Assert.Equal(elementId, row.ElementKey);
        Assert.Contains("\"EventName\":\"PhoneFieldCompleted\"", row.MetadataJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("forged_purchase", row.MetadataJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"eventName\":\"Purchase\"", row.MetadataJson ?? "", StringComparison.Ordinal);

        request.ClientEventId = Guid.NewGuid();
        request.EventType = "cta_click";
        var rejected = await controller.Ingest(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Single(await f.Db.AnalyticsEvents.ToListAsync());
    }

    [Fact]
    public async Task DistinctSubmissionsSameEmailKeepDistinctInquiryIntakeConversionAndRelationships()
    {
        using var f = new Fixture(); await f.SeedPublishedAsync();
        var request = f.Request();
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request with { SubmissionId = Guid.NewGuid(), Message = "A separate inquiry" }, default));
        Assert.Equal(2, await f.Db.Set<CommerceWebsiteInquiry>().CountAsync());
        Assert.Equal(2, await f.Db.WebsiteLeads.CountAsync());
        Assert.Equal(2, await f.Db.WebsiteLeadIntakeLinks.CountAsync());
        var profiles = await f.Db.WorkstationLeadProfiles.ToListAsync();
        Assert.Equal(2, profiles.Count);
        var links = await f.Db.WebsiteLeadIntakeLinks.ToListAsync();
        Assert.Equal(2, links.Select(x => x.WorkstationLeadId).Distinct().Count());
        Assert.Equal(2, links.Select(x => x.WebsiteLeadPublicId).Distinct().Count());
        Assert.All(links, link =>
        {
            Assert.Equal(f.BusinessId, link.CommerceBusinessId);
            Assert.Equal(f.BusinessId, Assert.Single(profiles, x => x.LeadId == link.WorkstationLeadId).CommerceBusinessId);
        });
        var events = await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync();
        Assert.Equal(2, events.Count); Assert.Equal(2, events.Select(x => x.EventId).Distinct().Count());
        Assert.Equal(2, events.Select(x => x.ClientEventId).Distinct().Count());
        Assert.Equal(2, events.Select(x => CanonicalAdvertisingEventProjection.ReadString(x.MetadataJson, "canonicalOutcomeEventId")).Distinct().Count());
        Assert.All(events, row => Assert.Equal(f.BusinessId, row.CommerceBusinessId));
    }

    [Fact]
    public async Task RetryKeepsCanonicalEventIdentityAndOriginalEventTime()
    {
        using var f = new Fixture(); await f.SeedPublishedAsync();
        var request = f.Request(); Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        var initial = Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        var identity = (initial.EventId, initial.ClientEventId, initial.EventUtc, initial.ReceivedUtc,
            CanonicalAdvertisingEventProjection.ReadString(initial.MetadataJson, "canonicalOutcomeEventId"));
        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(lead.LeadId, initial.ClientEventId);
        Assert.Equal(CanonicalLeadEventIdentity.Resolve(lead), identity.Item5);
        Assert.Equal(lead.LeadId.ToString(), CanonicalAdvertisingEventProjection.ReadString(initial.MetadataJson, "WebsiteLeadId"));
        Assert.Equal(lead.CreatedUtc, initial.EventUtc);
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        var replay = Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(identity, (replay.EventId, replay.ClientEventId, replay.EventUtc, replay.ReceivedUtc,
            CanonicalAdvertisingEventProjection.ReadString(replay.MetadataJson, "canonicalOutcomeEventId")));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("consent")]
    [InlineData("phone")]
    [InlineData("name")]
    public async Task InvalidInputCreatesNoLeadCrmIntakeOrConversion(string invalid)
    {
        using var f = new Fixture(); await f.SeedPublishedAsync(); var request = f.Request();
        request = invalid switch { "email" => request with { Email = "not-an-email" }, "consent" => request with { Consent = false },
            "phone" => request with { Phone = "123" }, _ => request with { FirstName = "" } };
        Assert.IsType<BadRequestObjectResult>(await f.Controller.Submit(request, default));
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync()); Assert.Empty(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Empty(await f.Db.WorkstationLeadProfiles.ToListAsync()); Assert.Empty(await f.Db.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.Empty(await f.Db.AnalyticsEvents.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessSenderFailureLeavesDurableCrmAndDueRetryUsesCurrentRecipient(bool throws)
    {
        using var f = new Fixture(); await f.SeedPublishedAsync();
        var business = await f.Db.CommerceBusinesses.SingleAsync(x => x.Id == f.BusinessId);
        business.OwnerEmail = "old-owner@example.org";
        await f.Db.SaveChangesAsync();
        var send = f.EmailSender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (throws) send.ThrowsAsync(new InvalidOperationException("Fixture transport failure")); else send.ReturnsAsync(false);
        var request = f.Request();
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        var inquiry = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
        Assert.Equal("Pending", inquiry.NotificationStatus); Assert.Equal(0, inquiry.NotificationAttempts);
        f.EmailSender.VerifyNoOtherCalls();
        await f.DeliverPendingNotificationsAsync();
        Assert.Equal("RetryPending", inquiry.NotificationStatus); Assert.Null(inquiry.NotificationSentUtc); Assert.Equal(1, inquiry.NotificationAttempts);
        Assert.Single(await f.Db.WebsiteLeads.ToListAsync()); Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Single(await f.Db.WebsiteLeadIntakeLinks.ToListAsync()); Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        await f.DeliverPendingNotificationsAsync();
        Assert.Equal(1, inquiry.NotificationAttempts);
        business.OwnerEmail = "current-owner@example.org"; inquiry.NotificationNextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        await f.Db.SaveChangesAsync(); send.ReturnsAsync(true);
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        Assert.Equal(1, inquiry.NotificationAttempts);
        await f.DeliverPendingNotificationsAsync();
        Assert.Equal("Sent", inquiry.NotificationStatus); Assert.Equal(2, inquiry.NotificationAttempts);
        Assert.IsType<OkObjectResult>(await f.Controller.Submit(request, default));
        await f.DeliverPendingNotificationsAsync();
        f.EmailSender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        f.EmailSender.Verify(x => x.TrySendAsync("current-owner@example.org", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), request.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(await f.Db.WebsiteLeads.ToListAsync()); Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableReceiptAndReplayDoNotWaitForEmail_AndWorkerUsesCurrentOwnerAfterRequestEnds(bool businessInquiry)
    {
        using var f = new Fixture(businessInquiry ? "https://business.example" : "https://www.mylegnd.com");
        if (businessInquiry)
        {
            await f.SeedPublishedAsync();
            (await f.Db.CommerceBusinesses.SingleAsync()).OwnerEmail = "initial-owner@example.org";
            await f.Db.SaveChangesAsync();
        }
        else
        {
            await f.SeedLegendPublishedAsync();
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.EmailSender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (string to, string subject, string html, string? text, string? reply, bool save, CancellationToken ct) =>
            {
                entered.TrySetResult();
                return await release.Task.WaitAsync(ct);
            });

        static void AssertReceipt(IActionResult result, bool sent)
        {
            var accepted = Assert.IsType<OkObjectResult>(result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(accepted.Value));
            Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal(sent, json.RootElement.GetProperty("notificationSent").GetBoolean());
        }

        var request = f.Request() with { SourceActionKey = businessInquiry ? "business_contact" : "legend_contact" };
        using var requestLifetime = new CancellationTokenSource();
        var submitted = f.Controller.Submit(request, requestLifetime.Token);
        Task? delivery = null;
        try
        {
            AssertReceipt(await submitted.WaitAsync(TimeSpan.FromSeconds(10)), false);
            AssertReceipt(await f.Controller.Submit(request, CancellationToken.None), false);
            f.EmailSender.VerifyNoOtherCalls();
            var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
            Assert.Null(lead.NotificationAttemptUtc);
            Assert.Null(lead.NotificationSentUtc);
            Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
            Assert.Single(await f.Db.WebsiteLeadIntakeLinks.ToListAsync());
            Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
            Assert.Equal(businessInquiry ? 1 : 0, await f.Db.Set<CommerceWebsiteInquiry>().CountAsync());

            if (businessInquiry)
                (await f.Db.CommerceBusinesses.SingleAsync()).OwnerEmail = "current-owner@example.org";
            else
                (await f.Db.AgentProfiles.SingleAsync()).NormalizedEmail = "current-owner@example.org";
            await f.Db.SaveChangesAsync();
            requestLifetime.Cancel();

            delivery = f.DeliverPendingNotificationsAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(delivery.IsCompleted);
            release.TrySetResult(true);
            await delivery.WaitAsync(TimeSpan.FromSeconds(10));
            AssertReceipt(await f.Controller.Submit(request, CancellationToken.None), true);
            await f.DeliverPendingNotificationsAsync();
            f.EmailSender.Verify(x => x.TrySendAsync(
                "current-owner@example.org",
                businessInquiry ? "New website inquiry" : "New LEGEND® website inquiry",
                It.IsAny<string>(), It.IsAny<string?>(), request.Email,
                !businessInquiry, It.IsAny<CancellationToken>()), Times.Once);
            f.EmailSender.VerifyNoOtherCalls();
            Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
            Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        }
        finally
        {
            release.TrySetResult(true);
            await submitted.WaitAsync(TimeSpan.FromSeconds(10));
            if (delivery is not null) await delivery.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LocalMvcJsonRouteAcceptsCanonicalBodyAndRejectsMalformedOrWrongContentType()
    {
        using var f = new Fixture(); await f.SeedPublishedAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(WebsiteInquiriesController).Assembly).AddControllersAsServices();
        builder.Services.AddSingleton(f.Controller);
        builder.Services.AddRateLimiter(PlatformRateLimiting.ConfigurePolicies);
        await using var app = builder.Build();
        app.UseRouting(); app.UseRateLimiter(); app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpResponseMessage> Post(string body, string contentType)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/website-inquiries/public");
                request.Headers.Add("Origin", "https://business.example");
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
                return await client.SendAsync(request);
            }
            var json = JsonSerializer.Serialize(f.Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var accepted = await Post(json, "application/json");
            Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
            using var receipt = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            Assert.True(receipt.RootElement.GetProperty("accepted").GetBoolean());
            using var malformed = await Post("{", "application/json"); Assert.Equal(System.Net.HttpStatusCode.BadRequest, malformed.StatusCode);
            using var empty = await Post("null", "application/json"); Assert.Equal(System.Net.HttpStatusCode.BadRequest, empty.StatusCode);
            using var wrongType = await Post(json, "text/plain"); Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
            Assert.Single(await f.Db.WebsiteLeads.ToListAsync()); Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
            Assert.Single(await f.Db.AnalyticsEvents.Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid BusinessId { get; } = Guid.NewGuid();
        public Guid VersionId { get; } = Guid.NewGuid();
        public WebsiteInquiriesController Controller { get; }
        public Mock<IWebsiteInquiryEmailSender> EmailSender { get; } = new();
        private readonly WebsiteIntakeRecipientResolver _recipients;
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        public Fixture(string origin = "https://business.example")
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string?>("Founder:Upn", "founder@example.org")
            }).Build();
            var domains = new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), config);
            var scopes = new PublicWebsiteRuntimeScopeResolver(Db, domains, config);
            _recipients = new WebsiteIntakeRecipientResolver(Db, config);
            EmailSender.Setup(sender => sender.TrySendAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Controller = new(
                Db,
                _tickets,
                config,
                scopes,
                new WebsiteLifeLeadCaptureService(Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebsiteLifeLeadCaptureService>.Instance),
                _recipients)
                { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            Controller.Request.Headers.Origin = origin;
        }
        public async Task DeliverPendingNotificationsAsync(CancellationToken cancellationToken = default)
        {
            await new BusinessInquiryNotificationService(Db, _recipients, EmailSender.Object)
                .DeliverPendingAsync(cancellationToken);
            using var provider = new ServiceCollection()
                .AddSingleton(Db)
                .AddSingleton(_recipients)
                .AddSingleton(EmailSender.Object)
                .BuildServiceProvider();
            using var worker = new WebsiteLeadNotificationRecoveryWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WebsiteLeadNotificationRecoveryWorker>.Instance);
            await worker.RecoverPendingAsync(cancellationToken);
        }

        public WebsiteInquiriesController.PublicRequest Request() => new(
            Guid.NewGuid(), "Visitor", "Example", "(602) 555-0199", "visitor@example.org", "Please contact me.", "/contact", true,
            SourceActionKey: "business_contact", SessionId: "business-session", VisitorId: "business-visitor",
            UtmSource: "meta", UtmCampaign: "campaign-one", Fbclid: "fbclid-one");
        public async Task SeedLegendPublishedAsync()
        {
            Db.Add(new AgentProfile
            {
                AgentUserId = "founder-user",
                AgentUpn = "founder@example.org",
                NormalizedEmail = "founder@example.org",
                FullName = "Founder",
                UpdatedUtc = DateTime.UtcNow
            });
            var state = new WebsiteContentState
            {
                OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
                SiteKey = WebsiteEditorSiteKeys.Legend,
                PublishedVersionId = VersionId
            };
            Db.Add(state);
            Db.Add(new WebsiteContentVersion { Id = VersionId, StateId = state.Id });
            await Db.SaveChangesAsync();
        }

        public async Task SeedPublishedAsync()
        {
            Db.Add(new CommerceBusiness { Id = BusinessId, Key = "business", DisplayName = "Business" });
            Db.Add(new CommerceBusinessStorefrontSettings { CommerceBusinessId = BusinessId });
            Db.Add(new WebsiteDomainBinding { CommerceBusinessId = BusinessId, Hostname = "business.example", Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
            var state = new WebsiteContentState { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(BusinessId), SiteKey = WebsiteEditorSiteKeys.Business, PublishedVersionId = VersionId };
            Db.Add(state);
            Db.Add(new WebsiteContentVersion
            {
                Id = VersionId,
                StateId = state.Id,
                CompiledPagesJson = "{\"pages\":{\"/\":{\"html\":\"published\"},\"/contact\":{\"html\":\"published\"}}}"
            });
            await Db.SaveChangesAsync();
        }
        public string Ticket(ClientProfile profile) => _tickets.Protect(new WebsiteEditorTicket(
            WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(BusinessId), null, false,
            DateTime.UtcNow.AddMinutes(10), BusinessId, ActorUserId: profile.ClientUserId, ActorClientProfileId: profile.Id));
        public void Dispose() { _tickets.Dispose(); Db.Dispose(); }
    }
}
