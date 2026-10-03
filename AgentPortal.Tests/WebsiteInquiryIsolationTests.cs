using Microsoft.Extensions.DependencyInjection;
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
    public async Task BusinessInquiryImmediatelyUsesCurrentScopedRecipient_AndCanonicalV3LeadRemainsServerAuthorityEligible()
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
        Assert.Contains("\"notificationSent\":true", resultJson, StringComparison.OrdinalIgnoreCase);

        var inquiry = Assert.Single(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());
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

        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "founder@example.org",
            It.Is<string>(subject => subject.Contains("LEGEND", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(),
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

        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "primary.advisor@example.org",
            It.Is<string>(subject => subject.Contains("Protection", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "visitor@example.org",
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ParfaitContactUsesCanonicalBusinessInquiryLeadCrmAnalyticsAndPrimaryEmail()
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

        var result = Assert.IsType<OkObjectResult>(await f.Controller.Submit(
            f.Request() with
            {
                SourcePath = "/Contact",
                SourceActionKey = "parfait_contact",
                SessionId = "parfait-session",
                VisitorId = "parfait-visitor"
            },
            CancellationToken.None));
        Assert.Contains("\"accepted\":true", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await f.Db.Set<CommerceWebsiteInquiry>().ToListAsync());

        var lead = Assert.Single(await f.Db.WebsiteLeads.ToListAsync());
        Assert.Equal(businessId, lead.CommerceBusinessId);
        Assert.Null(lead.AgentTrackingProfileId);
        Assert.Equal("BusinessInquiry", lead.InterestType);

        var crm = Assert.Single(await f.Db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal(businessId, crm.CommerceBusinessId);
        Assert.Equal(string.Empty, crm.AgentUserId);

        var analytics = Assert.Single(await f.Db.AnalyticsEvents
            .Where(x => x.EventType == "website_lead_submitted").ToListAsync());
        Assert.Equal(businessId, analytics.CommerceBusinessId);
        Assert.Null(analytics.AgentTrackingProfileId);

        f.EmailSender.Verify(sender => sender.TrySendAsync(
            "parfait-primary@example.org",
            It.Is<string>(subject => subject.Contains("Parfait", StringComparison.OrdinalIgnoreCase)),
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
        // A reused event ID cannot move from a business to the root Protect owner.
        request.SiteKey = WebsiteEditorSiteKeys.Protect;
        Assert.IsType<ConflictObjectResult>(await controller.Ingest(request, default));
        request.SiteKey = WebsiteEditorSiteKeys.Business;
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

    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid BusinessId { get; } = Guid.NewGuid();
        public Guid VersionId { get; } = Guid.NewGuid();
        public WebsiteInquiriesController Controller { get; }
        public Mock<IWebsiteInquiryEmailSender> EmailSender { get; } = new();
        private readonly WebsiteEditorTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
        public Fixture(string origin = "https://business.example")
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string?>("Founder:Upn", "founder@example.org")
            }).Build();
            var domains = new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), config);
            var scopes = new PublicWebsiteRuntimeScopeResolver(Db, domains, config);
            var recipients = new WebsiteIntakeRecipientResolver(Db, config);
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
                recipients,
                EmailSender.Object)
                { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            Controller.Request.Headers.Origin = origin;
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
