using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Analytics;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Protect_Website.Controllers;
using ProtectWebsite.Services.Booking;
using ProtectWebsite.Services.Tracking;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    // Real controller handlers, SQLite transaction and capture authority. External
    // messaging/booking/analytics are stubs; this is not HTTP/browser acceptance.
    [Theory]
    [InlineData("LifeQuoteController", "SubmitLifeQuote", "life_general", "quote_life")]
    [InlineData("LifeQuoteController", "SubmitTermLifeQuote", "life_term", "quote_term_life")]
    [InlineData("LifeQuoteController", "SubmitWholeLifeQuote", "life_whole", "quote_whole_life")]
    [InlineData("LifeQuoteController", "SubmitFinalExpenseQuote", "life_finalexpense", "quote_final_expense")]
    [InlineData("LifeQuoteController", "SubmitMortgageQuote", "life_mp", "quote_mortgage_protection")]
    [InlineData("LifeQuoteController", "SubmitIulQuote", "life_iul", "quote_iul")]
    [InlineData("AutoQuoteController", "Auto", "auto", "quote_auto")]
    [InlineData("HomeQuoteController", "SubmitHomeQuote", "home", "quote_home")]
    [InlineData("CommercialQuoteController", "Commercial", "commercial", "quote_commercial")]
    [InlineData("DisabilityQuoteController", "SubmitDisabilityQuote", "disability", "quote_disability")]
    [InlineData("DentalVisionHearingQuoteController", "SubmitDentalVisionHearingQuote", "dental_vision_hearing", "quote_dvh")]
    [InlineData("RiskAssessmentController", "SubmitRiskAssessment", "risk_assessment", "risk_assessment")]
    public Task InsuranceHandlerFilesExactSubmissionAndRetryKeepsOneTile(string controllerName, string actionName, string product, string pageKey)
        => VerifyInsuranceSubmission(controllerName, actionName, product, pageKey, false);

    private async Task VerifyInsuranceSubmission(string controllerName, string actionName, string product, string pageKey, bool hosted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "synthetic-owner", AgentUpn = "synthetic@example.test", Slug = "synthetic-route-owner", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        db.Add(profile);
        db.Add(new AgentProfile { AgentUserId = profile.AgentUserId, AgentUpn = profile.AgentUpn, NormalizedEmail = profile.AgentUpn, FullName = "Synthetic Owner", UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AzureAd:TenantId"]="synthetic", ["AzureAd:ClientId"]="synthetic", ["AzureAd:ClientSecret"]="synthetic-unused",
            ["Contact:SenderEmail"]="", ["Founder:Upn"]="founder@example.test", ["Tracking:ApiBase"]="https://portal.example.test"
        }).Build();
        var pixel = new Mock<IMetaPixelResolutionService>();
        pixel.Setup(x=>x.ResolveForLeadAsync(It.IsAny<Guid?>(),It.IsAny<string?>(),It.IsAny<bool>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext { PixelOwnerType=MetaPixelOwnerTypes.Agent, AgentTrackingProfileId=profile.Id, AgentSlug=profile.Slug });
        var booking = new Mock<IPublicBookingResolver>();
        booking.Setup(x=>x.ResolveAsync(It.IsAny<PublicBookingResolveContext>(),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicBookingResolution(false,null,null,true,false,"disabled",PublicBookingConfigurationSources.None,profile.Id,profile.AgentUserId,profile.Slug,null,null,null));
        var email = new Mock<IWebsiteInquiryEmailSender>();
        email.Setup(x=>x.TrySendAsync(It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string?>(),It.IsAny<string?>(),It.IsAny<bool>(),It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var known = new Dictionary<Type,object> {
            [typeof(IConfiguration)] = config, [typeof(MasterAppDbContext)] = db,
            [typeof(AgentTrackingResolver)] = new AgentTrackingResolver(db,NullLogger<AgentTrackingResolver>.Instance),
            [typeof(WebsiteIntakeRecipientResolver)] = new WebsiteIntakeRecipientResolver(db,config),
            [typeof(IWebsiteLifeLeadCaptureService)] = new WebsiteLifeLeadCaptureService(db,NullLogger<WebsiteLifeLeadCaptureService>.Instance),
            [typeof(IMetaPixelResolutionService)] = pixel.Object, [typeof(IPublicBookingResolver)] = booking.Object,
            [typeof(IWebsiteInquiryEmailSender)] = email.Object
        };
        var type = typeof(LifeQuoteController).Assembly.GetType("Protect_Website.Controllers."+controllerName)!;
        var action = type.GetMethods().Single(x=>x.Name==actionName && x.GetCustomAttributes<HttpPostAttribute>().Any());
        var modelType = action.GetParameters().Single().ParameterType;
        object Dependency(Type dependency) => known.TryGetValue(dependency,out var value) ? value :
            dependency.IsGenericType && dependency.GetGenericTypeDefinition()==typeof(ILogger<>)
                ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(dependency.GenericTypeArguments))!
                : ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(dependency))!).Object;
        var controller = (Controller)Activator.CreateInstance(type,type.GetConstructors().Single().GetParameters().Select(x=>Dependency(x.ParameterType)).ToArray())!;
        var protector = new EphemeralDataProtectionProvider();
        using var services = new ServiceCollection().AddSingleton<IDataProtectionProvider>(protector).BuildServiceProvider();
        var field = controllerName=="LifeQuoteController" ? "MarketingEmailConsent" : "AcknowledgedDisclaimer";
        var requestId = Guid.NewGuid().ToString("N");
        var http = new DefaultHttpContext { RequestServices=services };
        http.Request.Method="POST";http.Request.Host=new HostString("synthetic.example.test");
        http.Request.ContentType="application/x-www-form-urlencoded";http.Request.Headers["X-Requested-With"]="fetch";
        http.Request.Form=new FormCollection(new Dictionary<string,StringValues> {
            ["SubmissionId"]=requestId, ["AcknowledgedDisclaimer"]="true", ["MarketingEmailConsent"]="true",
            ["HistoryDisclosureToken"]=WebsiteSubmissionEvidence.ProtectAuthorization(protector,modelType.FullName!,field,"Original synthetic disclosure")
        });
        http.Items["TrackingProfile"]=profile;http.Items["TrackingSlug"]=profile.Slug;http.Items["IsFounderPath"]=false;
        controller.ControllerContext=new ControllerContext { HttpContext=http };
        controller.Url=Mock.Of<IUrlHelper>();
        controller.TempData=new TempDataDictionary(http,Mock.Of<ITempDataProvider>());
        const string answers="""
            {"FirstName":"Synthetic","LastName":"Applicant","InsuredFirstName":"Synthetic","InsuredLastName":"Applicant",
             "Email":"synthetic@example.test","EmailAddress":"synthetic@example.test","BusinessEmail":"synthetic@example.test",
             "Phone":"6025550100","PhoneNumber":"6025550100","BusinessPhone":"6025550100","State":"AZ","AddressState":"AZ","PostalCode":"85001",
             "Age":34,"AgeRange":"25-34","MarketingEmailConsent":true,"AcknowledgedDisclaimer":true,"ProtectingWho":"family",
             "CoverageGoal":"replace_income","CoverageAmountOption":"250000","CoverageAmount":250000,"TobaccoUse":"non_smoker",
             "PrimaryConcern":"coverage","HouseholdSize":"1","HasLifeInsurance":"No","HasDI":"No","AnnualIncome":0,
             "Drivers":[{"FirstName":"Synthetic","LastName":"One"},{"FirstName":"Synthetic","LastName":"Two"}],
             "Vehicles":[{"Make":"Synthetic","Model":"One"},{"Make":"Synthetic","Model":"Two"}]}
            """;
        var answerJson=controllerName=="RiskAssessmentController" ? answers.Replace("\"HouseholdSize\":\"1\"", "\"HouseholdSize\":1", StringComparison.Ordinal) : answers;
        using var httpFixture=hosted ? await InsuranceHttpFixture.Create(type,action,modelType,known,Dependency,protector,profile,CompleteInsuranceFixture(answerJson,controllerName),http) : null;
        async Task<IActionResult> Submit() => httpFixture is not null ? await httpFixture.Submit(http.Request.Form) :
            await (Task<IActionResult>)action.Invoke(controller,[JsonSerializer.Deserialize(answerJson,modelType)!])!;
        var result=await Submit();
        Assert.True(result is OkObjectResult or RedirectToActionResult or RedirectResult, result.GetType().Name);
        var submission=await db.WebsiteLeads.SingleAsync();
        var link=await db.WebsiteLeadIntakeLinks.SingleAsync();
        var tile=await db.Set<CrmHistoryEntry>().SingleAsync(x=>x.Type=="FormSubmission");
        Assert.Equal(product,link.ProductType);Assert.Equal(pageKey,link.SourcePageKey);
        Assert.Equal(submission.LeadId,link.WebsiteLeadPublicId);
        Assert.Equal(link.Id,tile.SubmissionId);
        Assert.Equal(Infrastructure.Crm.CrmHistoryStore.LeadKey(link.WorkstationLeadId!),tile.RelationshipKey);
        using var snapshot=JsonDocument.Parse(link.SnapshotJson!);
        var evidence=snapshot.RootElement.GetProperty("SubmittedFormEvidence");
        Assert.Equal("Original synthetic disclosure",evidence.GetProperty("authorizations")[0].GetProperty("text").GetString());
        Assert.Equal("Synthetic",evidence.GetProperty("answers").GetProperty(controllerName=="CommercialQuoteController" ? "InsuredFirstName" : "FirstName").GetString());
        Assert.Equal("Checked on submitted form",evidence.GetProperty("authorizations")[0].GetProperty("status").GetString());
        if(controllerName=="AutoQuoteController")
        {
            var captured=evidence.GetProperty("answers");
            Assert.Equal(2,captured.GetProperty("Drivers").GetArrayLength());
            Assert.Equal("Two",captured.GetProperty("Drivers")[1].GetProperty("LastName").GetString());
            Assert.Equal(2,captured.GetProperty("Vehicles").GetArrayLength());
            Assert.Equal("Two",captured.GetProperty("Vehicles")[1].GetProperty("Model").GetString());
        }
        if(controllerName=="RiskAssessmentController")
            Assert.Equal(0,evidence.GetProperty("answers").GetProperty("AnnualIncome").GetDecimal());
        var history=BuildController(db,profile.AgentUserId);
        var list=Assert.IsType<OkObjectResult>(await history.Entries("lead",link.WorkstationLeadId!,null,null,null,null,null,null,null,null,null));
        using(var listed=JsonDocument.Parse(JsonSerializer.Serialize(list.Value)))
            Assert.Contains(listed.RootElement.GetProperty("entries").EnumerateArray(),x=>x.GetProperty("Id").GetGuid()==tile.Id);
        Assert.IsType<OkObjectResult>(await history.Entry(tile.Id,"lead",link.WorkstationLeadId!));
        var original=link.SnapshotJson;
        await Submit();
        Assert.Equal(1,await db.WebsiteLeads.CountAsync());
        Assert.Equal(1,await db.WebsiteLeadIntakeLinks.CountAsync());
        Assert.Equal(1,await db.Set<CrmHistoryEntry>().CountAsync(x=>x.Type=="FormSubmission"));
        Assert.Equal(original,(await db.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync()).SnapshotJson);
        var repeat=http.Request.Form.ToDictionary(x=>x.Key,x=>x.Value);repeat["SubmissionId"]=Guid.NewGuid().ToString("N");
        http.Request.Form=new FormCollection(repeat);
        var repeated=await Submit();Assert.True(repeated is OkObjectResult or RedirectToActionResult or RedirectResult,repeated.GetType().Name);
        Assert.Equal(2,await db.WebsiteLeads.CountAsync());Assert.Equal(2,await db.Set<CrmHistoryEntry>().CountAsync(x=>x.Type=="FormSubmission"));

    }
}
