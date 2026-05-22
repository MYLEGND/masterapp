using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Leads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Protect_Website.Controllers;
using Protect_Website.Models;
using ProtectWebsite.Services.Booking;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services.Tracking;
using Xunit;

namespace AgentPortal.Tests;

public class LifeQuoteControllerPublicBookingTests
{
    [Fact]
    public async Task ActivatePublicBookingExperience_WhenEnabledAndLinkedLeadExists_CreatesRequestedAppointment()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var intakeLinkId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();
        const string workstationLeadId = "lead-123";
        const string agentUserId = "agent-user-123";

        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = workstationLeadId,
            AgentUserId = agentUserId,
            Bucket = "LifeInsurance",
            OriginalLeadType = "LifeInsurance",
            FirstName = "Jordan",
            LastName = "Miles",
            Email = "jordan@example.test",
            Phone = "5551112222",
            CrmOrder = DateTime.UtcNow.Ticks
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeLinkId,
            WebsiteLeadRowId = 101,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = workstationLeadId,
            AgentUserId = agentUserId,
            Bucket = "LifeInsurance",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var protector = BuildBookingProtector();
        var publicBookingResolver = new Mock<IPublicBookingResolver>();
        publicBookingResolver
            .Setup(resolver => resolver.ResolveAsync(It.IsAny<PublicBookingResolveContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(
                Enabled: true,
                EmbedUrl: "https://outlook.office.com/bookings/embed",
                FallbackUrl: "https://outlook.office.com/bookings/fallback",
                PreferModalOnMobile: true,
                IsAgentOverride: false,
                Reason: "configured",
                ConfigurationSource: PublicBookingConfigurationSources.GlobalFallback,
                AgentTrackingProfileId: null,
                AgentUserId: agentUserId,
                AgentSlug: "legend",
                CalendarUserId: null,
                CalendarEmail: "legend-calendar@example.test",
                BookingPageIdOrMailbox: "legend-mailbox"));
        var controller = BuildController(
            db,
            publicBookingResolver: publicBookingResolver.Object,
            publicBookingContextProtector: protector);

        var token = protector.Protect(new PublicBookingContext(
            WebsiteLeadId: websiteLeadId,
            AgentSlug: "legend",
            QuoteType: "life",
            PageKey: "quote_life",
            IssuedUtc: DateTime.UtcNow));

        var result = await controller.ActivatePublicBookingExperience(new LifeQuoteController.PublicBookingExperienceRequest
        {
            ContextToken = token,
            Surface = LeadAppointmentBookingSources.WebsiteEmbed
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(GetRequiredProperty(payload.RootElement, "enabled").GetBoolean());
        Assert.True(GetRequiredProperty(payload.RootElement, "canEmbed").GetBoolean());
        Assert.True(GetRequiredProperty(payload.RootElement, "linkedToLead").GetBoolean());
        Assert.Equal("Requested", GetRequiredProperty(payload.RootElement, "appointmentStatus").GetString());

        var appointment = Assert.Single(db.LeadAppointments);
        Assert.Equal(LeadAppointmentStatus.Requested, appointment.Status);
        Assert.Equal(LeadAppointmentBookingSources.WebsiteEmbed, appointment.BookingSource);
        Assert.Equal(intakeLinkId, appointment.WebsiteLeadIntakeLinkId);
        Assert.Equal(workstationLeadId, appointment.WorkstationLeadId);
        Assert.Equal(agentUserId, appointment.OwnerAgentUserId);
        Assert.NotNull(appointment.RequestedUtc);
        Assert.Null(appointment.BookedUtc);
    }

    [Fact]
    public async Task ActivatePublicBookingExperience_WhenDisabled_DoesNotCreateAppointment()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var protector = BuildBookingProtector();
        var publicBookingResolver = new Mock<IPublicBookingResolver>();
        publicBookingResolver
            .Setup(resolver => resolver.ResolveAsync(It.IsAny<PublicBookingResolveContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(
                Enabled: false,
                EmbedUrl: null,
                FallbackUrl: null,
                PreferModalOnMobile: true,
                IsAgentOverride: false,
                Reason: "disabled",
                ConfigurationSource: PublicBookingConfigurationSources.None,
                AgentTrackingProfileId: null,
                AgentUserId: null,
                AgentSlug: "legend",
                CalendarUserId: null,
                CalendarEmail: null,
                BookingPageIdOrMailbox: null));
        var controller = BuildController(
            db,
            publicBookingResolver: publicBookingResolver.Object,
            publicBookingContextProtector: protector);

        var token = protector.Protect(new PublicBookingContext(
            WebsiteLeadId: Guid.NewGuid(),
            AgentSlug: "legend",
            QuoteType: "life",
            PageKey: "quote_life",
            IssuedUtc: DateTime.UtcNow));

        var result = await controller.ActivatePublicBookingExperience(new LifeQuoteController.PublicBookingExperienceRequest
        {
            ContextToken = token,
            Surface = LeadAppointmentBookingSources.WebsiteModal
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.False(GetRequiredProperty(payload.RootElement, "enabled").GetBoolean());
        Assert.False(GetRequiredProperty(payload.RootElement, "linkedToLead").GetBoolean());
        Assert.Equal("disabled", GetRequiredProperty(payload.RootElement, "reason").GetString());
        Assert.Empty(db.LeadAppointments);
    }

    [Fact]
    public async Task SubmitLifeQuote_Ajax_WhenBookingDisabled_StillReturnsSuccess()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var captureService = new Mock<IWebsiteLifeLeadCaptureService>();
        captureService
            .Setup(service => service.UpsertAsync(It.IsAny<WebsiteLifeLeadCaptureRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebsiteLifeLeadCaptureResult(
                Captured: false,
                Created: false,
                WorkstationLeadId: null,
                Bucket: "LifeInsurance",
                AgentUserId: null,
                Reason: "NoAgentOwner"));

        var metaConversions = new Mock<IMetaConversionsApiService>();
        metaConversions
            .Setup(service => service.SendLeadAsync(It.IsAny<MetaLeadConversionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaConversionsApiResult
            {
                Attempted = false,
                Sent = false,
                Status = "skipped_not_configured",
                Note = "meta_config_missing"
            });

        var metaPixelResolution = new Mock<IMetaPixelResolutionService>();
        metaPixelResolution
            .Setup(service => service.ResolveForLeadAsync(It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext
            {
                PixelOwnerType = MetaPixelOwnerTypes.None
            });

        var metaSignal = new Mock<IMetaSignalIntelligenceService>();
        metaSignal
            .Setup(service => service.RecordConfirmedLeadAsync(It.IsAny<MetaSignalConfirmedLeadRequest>(), It.IsAny<HttpContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MetaSignalProcessResult?)null);
        var publicBookingResolver = new Mock<IPublicBookingResolver>();
        publicBookingResolver
            .Setup(resolver => resolver.ResolveAsync(It.IsAny<PublicBookingResolveContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(
                Enabled: false,
                EmbedUrl: null,
                FallbackUrl: null,
                PreferModalOnMobile: true,
                IsAgentOverride: false,
                Reason: "disabled",
                ConfigurationSource: PublicBookingConfigurationSources.None,
                AgentTrackingProfileId: null,
                AgentUserId: null,
                AgentSlug: null,
                CalendarUserId: null,
                CalendarEmail: null,
                BookingPageIdOrMailbox: null));

        var controller = BuildController(
            db,
            metaConversionsApi: metaConversions.Object,
            metaPixelResolutionService: metaPixelResolution.Object,
            metaSignalIntelligenceService: metaSignal.Object,
            websiteLifeLeadCaptureService: captureService.Object,
            publicBookingResolver: publicBookingResolver.Object);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Headers["X-Requested-With"] = "fetch";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var result = await controller.SubmitLifeQuote(new LifeQuoteFormModel
        {
            FirstName = "Casey",
            Phone = "(555)555-1111",
            MarketingEmailConsent = true,
            ProtectingWho = "family",
            CoverageGoal = "replace_income",
            CoverageAmountOption = "250000",
            CoverageAmount = 250000,
            TobaccoUse = "non_smoker",
            Age = 35,
            AgeRange = "35-44",
            OfferKey = "life",
            ProductType = "life_general"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(GetRequiredProperty(payload.RootElement, "success").GetBoolean());
        Assert.True(payload.RootElement.TryGetProperty("booking", out var bookingPayload));
        Assert.False(GetRequiredProperty(bookingPayload, "eligible").GetBoolean());

        var lead = Assert.Single(db.WebsiteLeads);
        Assert.Equal("Casey", lead.FirstName);
        Assert.Equal("life_general", lead.InterestType);
        Assert.Equal("New", lead.Status);
    }

    [Fact]
    public async Task SubmitLifeQuote_Ajax_WhenValidationFails_ReturnsFieldErrorsAndCorrelationId()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = BuildController(db);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Headers["X-Requested-With"] = "fetch";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());

        var result = await controller.SubmitLifeQuote(new LifeQuoteFormModel
        {
            FirstName = "Casey",
            Phone = "(555)555-1111",
            MarketingEmailConsent = false,
            ProtectingWho = "family",
            CoverageGoal = "replace_income",
            CoverageAmountOption = "250000",
            CoverageAmount = 250000,
            TobaccoUse = "non_smoker",
            Age = 35,
            AgeRange = "35-44",
            OfferKey = "life",
            ProductType = "life_general"
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));

        Assert.Equal(
            "Please check the box so we can send your estimate and options.",
            GetRequiredProperty(payload.RootElement, "error").GetString());
        Assert.True(payload.RootElement.TryGetProperty("correlationId", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));

        var fieldErrors = GetRequiredProperty(payload.RootElement, "fieldErrors");
        Assert.True(fieldErrors.TryGetProperty(nameof(LifeQuoteFormModel.MarketingEmailConsent), out var consentErrors));
        Assert.Contains(
            "Please check the box so we can send your estimate and options.",
            consentErrors.EnumerateArray().Select(value => value.GetString()));
    }

    private static LifeQuoteController BuildController(
        Infrastructure.Data.MasterAppDbContext db,
        IMetaConversionsApiService? metaConversionsApi = null,
        IMetaPixelResolutionService? metaPixelResolutionService = null,
        IMetaSignalIntelligenceService? metaSignalIntelligenceService = null,
        IWebsiteLifeLeadCaptureService? websiteLifeLeadCaptureService = null,
        IPublicBookingResolver? publicBookingResolver = null,
        IPublicBookingConfirmationService? publicBookingConfirmationService = null,
        IPublicBookingContextProtector? publicBookingContextProtector = null)
    {
        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);

        return new LifeQuoteController(
            BuildConfig(),
            resolver,
            db,
            metaConversionsApi ?? Mock.Of<IMetaConversionsApiService>(),
            metaPixelResolutionService ?? Mock.Of<IMetaPixelResolutionService>(),
            metaSignalIntelligenceService ?? Mock.Of<IMetaSignalIntelligenceService>(),
            websiteLifeLeadCaptureService ?? Mock.Of<IWebsiteLifeLeadCaptureService>(),
            publicBookingResolver ?? Mock.Of<IPublicBookingResolver>(),
            publicBookingConfirmationService ?? Mock.Of<IPublicBookingConfirmationService>(),
            publicBookingContextProtector ?? BuildBookingProtector(),
            NullLogger<LifeQuoteController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:TenantId"] = "tenant",
                ["AzureAd:ClientId"] = "client",
                ["AzureAd:ClientSecret"] = "secret",
                ["Contact:SenderEmail"] = "",
                ["Contact:RecipientEmail"] = "",
                ["Tracking:ApiBase"] = "https://portal.example.test"
            })
            .Build();
    }

    private static IPublicBookingContextProtector BuildBookingProtector()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"booking-protector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        return new PublicBookingContextProtector(DataProtectionProvider.Create(new DirectoryInfo(keyPath)));
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string camelCaseName)
    {
        if (element.TryGetProperty(camelCaseName, out var value))
        {
            return value;
        }

        var pascalCaseName = string.IsNullOrWhiteSpace(camelCaseName)
            ? camelCaseName
            : char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];

        if (element.TryGetProperty(pascalCaseName, out value))
        {
            return value;
        }

        throw new KeyNotFoundException($"Property '{camelCaseName}' was not found.");
    }
}
