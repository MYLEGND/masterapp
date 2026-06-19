using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProtectWebsite.Services.Booking;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class PublicBookingConfirmationServiceTests
{
    [Fact]
    public async Task TryConfirmAsync_WithoutTrustedMatch_KeepsAppointmentRequestedAndPending()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var intakeLinkId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        SeedLinkedLead(db, websiteLeadId, intakeLinkId, appointmentId);
        await db.SaveChangesAsync();

        var matcher = new Mock<IPublicBookingCalendarMatcher>();
        matcher
            .Setup(x => x.TryMatchAsync(It.IsAny<PublicBookingCalendarMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicBookingCalendarMatchResult?)null);

        var resolver = new Mock<IPublicBookingResolver>();
        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<PublicBookingResolveContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(
                Enabled: true,
                EmbedUrl: "https://bookings.example.com/embed",
                FallbackUrl: "https://bookings.example.com/fallback",
                PreferModalOnMobile: true,
                IsAgentOverride: false,
                Reason: "agent_profile",
                ConfigurationSource: PublicBookingConfigurationSources.AgentProfile,
                AgentTrackingProfileId: null,
                AgentUserId: "agent-1",
                AgentSlug: "alpha",
                CalendarUserId: null,
                CalendarEmail: "calendar-alpha@example.com",
                BookingPageIdOrMailbox: "alpha-mailbox"));

        var service = new PublicBookingConfirmationService(
            db,
            matcher.Object,
            resolver.Object,
            NullLogger<PublicBookingConfirmationService>.Instance);

        var result = await service.TryConfirmAsync(new PublicBookingContext(
            WebsiteLeadId: websiteLeadId,
            AgentSlug: "alpha",
            QuoteType: "life",
            PageKey: "quote_life",
            IssuedUtc: DateTime.UtcNow));

        Assert.False(result.Verified);
        Assert.True(result.PendingConfirmation);
        Assert.True(result.LinkedToLead);
        Assert.Equal("Requested", result.AppointmentStatus);
        Assert.Equal("confirmation_not_verified", result.Reason);

        var appointment = await db.LeadAppointments.SingleAsync(x => x.Id == appointmentId);
        Assert.Equal(LeadAppointmentStatus.Requested, appointment.Status);
        Assert.Equal(LeadAppointmentBookingSources.WebsiteEmbed, appointment.BookingSource);
        Assert.Null(appointment.ConfirmationSource);
        Assert.Equal(PublicBookingConfigurationSources.AgentProfile, appointment.BookingConfigurationSource);
        Assert.Equal("calendar-alpha@example.com", appointment.BookingCalendarEmail);
        Assert.Equal("alpha-mailbox", appointment.BookingPageIdOrMailbox);
    }

    [Fact]
    public async Task TryConfirmAsync_WithTrustedCalendarMatch_PromotesRequestedAppointmentToBooked()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var intakeLinkId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        SeedLinkedLead(db, websiteLeadId, intakeLinkId, appointmentId);
        await db.SaveChangesAsync();

        var matcher = new Mock<IPublicBookingCalendarMatcher>();
        matcher
            .Setup(x => x.TryMatchAsync(It.IsAny<PublicBookingCalendarMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingCalendarMatchResult(
                EventId: "evt-123",
                WebLink: "https://outlook.test/events/evt-123",
                ScheduledStartUtc: new DateTime(2026, 5, 23, 16, 0, 0, DateTimeKind.Utc),
                ScheduledEndUtc: new DateTime(2026, 5, 23, 16, 30, 0, DateTimeKind.Utc),
                MeetingUrl: "https://teams.example.com/join/evt-123",
                MatchReason: "calendar_event_id"));

        var resolver = new Mock<IPublicBookingResolver>();
        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<PublicBookingResolveContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(
                Enabled: true,
                EmbedUrl: "https://bookings.example.com/embed",
                FallbackUrl: "https://bookings.example.com/fallback",
                PreferModalOnMobile: true,
                IsAgentOverride: false,
                Reason: "agent_profile",
                ConfigurationSource: PublicBookingConfigurationSources.AgentProfile,
                AgentTrackingProfileId: null,
                AgentUserId: "agent-1",
                AgentSlug: "alpha",
                CalendarUserId: null,
                CalendarEmail: "calendar-alpha@example.com",
                BookingPageIdOrMailbox: "alpha-mailbox"));

        var service = new PublicBookingConfirmationService(
            db,
            matcher.Object,
            resolver.Object,
            NullLogger<PublicBookingConfirmationService>.Instance);

        var result = await service.TryConfirmAsync(new PublicBookingContext(
            WebsiteLeadId: websiteLeadId,
            AgentSlug: "alpha",
            QuoteType: "life",
            PageKey: "quote_life",
            IssuedUtc: DateTime.UtcNow));

        Assert.True(result.Verified);
        Assert.False(result.PendingConfirmation);
        Assert.True(result.LinkedToLead);
        Assert.Equal("Booked", result.AppointmentStatus);
        Assert.Equal(LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch, result.BookingSource);
        Assert.Equal(LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch, result.ConfirmationSource);
        Assert.Equal("calendar_event_id", result.Reason);

        var appointment = await db.LeadAppointments.SingleAsync(x => x.Id == appointmentId);
        Assert.Equal(LeadAppointmentStatus.Booked, appointment.Status);
        Assert.Equal(LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch, appointment.BookingSource);
        Assert.Equal(LeadAppointmentBookingSources.MicrosoftGraphFallbackMatch, appointment.ConfirmationSource);
        Assert.Equal("evt-123", appointment.CalendarEventId);
        Assert.Equal("https://outlook.test/events/evt-123", appointment.CalendarEventWebLink);
        Assert.Equal("https://teams.example.com/join/evt-123", appointment.MeetingUrl);
        Assert.NotNull(appointment.BookedUtc);

        var analyticsEvent = await db.AnalyticsEvents.SingleAsync();
        Assert.Equal(AppointmentAnalyticsEventCatalog.Booked, analyticsEvent.EventType);
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "isBrowserSignal"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "isServerAuthority"));
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "metaServerAuthorityEligible"));
        Assert.False(MetaSignalSingleTruthPolicy.ReadBoolean(analyticsEvent.MetadataJson, "metaSingleTruthDispatchEligible"));
        Assert.Equal(
            MetaSignalEventCatalog.BuildEventKey(AppointmentAnalyticsEventCatalog.Booked, websiteLeadId, null),
            MetaSignalSingleTruthPolicy.ReadString(analyticsEvent.MetadataJson, "eventKey"));
    }

    private static void SeedLinkedLead(
        Infrastructure.Data.MasterAppDbContext db,
        Guid websiteLeadId,
        Guid intakeLinkId,
        Guid appointmentId)
    {
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-1",
            AgentUserId = "agent-1",
            Bucket = "LifeInsurance",
            OriginalLeadType = "LifeInsurance",
            FirstName = "Jordan",
            LastName = "Miles",
            Email = "jordan@example.com",
            Phone = "6025550100",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            Id = 1,
            LeadId = websiteLeadId,
            FirstName = "Jordan",
            LastName = "Miles",
            Email = "jordan@example.com",
            Phone = "6025550100",
            AgentSlug = "alpha",
            CreatedUtc = DateTime.UtcNow,
            Status = "New"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeLinkId,
            WebsiteLeadRowId = 1,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "lead-1",
            AgentUserId = "agent-1",
            Bucket = "LifeInsurance",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        db.LeadAppointments.Add(new LeadAppointment
        {
            Id = appointmentId,
            WorkstationLeadId = "lead-1",
            OwnerAgentUserId = "agent-1",
            WebsiteLeadIntakeLinkId = intakeLinkId,
            Status = LeadAppointmentStatus.Requested,
            BookingSource = LeadAppointmentBookingSources.WebsiteEmbed,
            RequestedBookingSource = LeadAppointmentBookingSources.WebsiteEmbed,
            RequestedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
    }
}
