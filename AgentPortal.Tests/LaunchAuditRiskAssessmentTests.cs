using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Protect_Website.Controllers;
using Protect_Website.Models;
using ProtectWebsite.Services.Communication;
using ProtectWebsite.Services.Tracking;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LaunchAuditRiskAssessmentTests
{
    [Fact]
    public async Task AssessmentRequiresConsentWithoutIncompatibleBooleanRangeValidation()
    {
        var property = typeof(RiskAssessmentModel).GetProperty(nameof(RiskAssessmentModel.AcknowledgedDisclaimer))!;
        Assert.Empty(property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), true));
        await using var db = ControllerTestHelpers.BuildDb();
        var sender = new Mock<IProtectEmailSender>(MockBehavior.Strict);
        var controller = new RiskAssessmentController(new ConfigurationBuilder().Build(), sender.Object, db,
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance),
            new WebsiteIntakeRecipientResolver(db, new ConfigurationBuilder().Build()),
            new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance),
            NullLogger<RiskAssessmentController>.Instance);
        var result = await controller.SubmitRiskAssessment(new RiskAssessmentModel {
            FirstName = "Test", LastName = "Only", Email = "controlled@example.test", AcknowledgedDisclaimer = false
        });
        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(nameof(RiskAssessmentModel.AcknowledgedDisclaimer), controller.ModelState.Keys);
        Assert.Empty(db.WebsiteLeads);
        Assert.Empty(db.AnalyticsEvents);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AssessmentRetryKeepsOneLeadCrmHandoffAndEventAndRetriesFailedNotification()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "test-agent", AgentUpn = "advisor@example.test", Slug = "test-agent", DisplayName = "Test Advisor" };
        db.AgentTrackingProfiles.Add(profile);
        await db.SaveChangesAsync();
        var sender = new Mock<IProtectEmailSender>();
        sender.SetupSequence(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false).ReturnsAsync(true);
        var notificationConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Founder:Upn"] = "founder@example.test"
            })
            .Build();
        var controller = new RiskAssessmentController(
            notificationConfig,
            sender.Object, db,
            new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance),
            new WebsiteIntakeRecipientResolver(db, notificationConfig),
            new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance),
            NullLogger<RiskAssessmentController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("protect.mylegnd.com");
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues> {
            ["SubmissionId"] = Guid.NewGuid().ToString(), ["SessionId"] = "test-session", ["VisitorId"] = "test-visitor"
        });
        http.Items["TrackingProfile"] = profile;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        var model = new RiskAssessmentModel { FirstName = "Test", LastName = "Only", Email = "controlled@example.test", AcknowledgedDisclaimer = true, HasLifeInsurance = "No", HasDI = "No" };
        Assert.IsType<ViewResult>(await controller.SubmitRiskAssessment(model));
        controller.ModelState.Clear();
        var saved = Assert.Single(db.WebsiteLeads);
        saved.NotificationAttemptUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.IsType<ViewResult>(await controller.SubmitRiskAssessment(model));
        controller.ModelState.Clear();
        saved.NotificationAttemptUtc = null;
        await db.SaveChangesAsync();
        Assert.IsType<RedirectToActionResult>(await controller.SubmitRiskAssessment(model));
        Assert.IsType<RedirectToActionResult>(await controller.SubmitRiskAssessment(model));
        var lead = Assert.Single(db.WebsiteLeads);
        Assert.Equal(profile.Id, lead.AgentTrackingProfileId);
        Assert.NotNull(lead.NotificationSentUtc);
        Assert.Single(db.WebsiteLeadIntakeLinks);
        Assert.Single(db.WorkstationLeadProfiles);
        Assert.Single(db.AnalyticsEvents);
        sender.Verify(x => x.TrySendAsync(profile.AgentUpn, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), model.Email, true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
