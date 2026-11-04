using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Leads;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.WebsiteRuntime;
using Microsoft.AspNetCore.Hosting;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class NativeTrainingPublicationTests
{
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public async Task OptionalTrainingUsesImmutablePublicationWithoutDeletingCatalog(bool hasPublication, bool published, bool deleted, bool visible)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait", Status = "Active", IsActive = true };
        var draft = new WebsiteContentDocument();
        draft.Pages["/store/training"] = new WebsitePageDocument { Navigation = new WebsitePageNavigation { IsPublished = true } };
        var publication = new WebsiteContentDocument();
        publication.Pages["/store/training"] = new WebsitePageDocument { Navigation = new WebsitePageNavigation { IsPublished = published, IsDeleted = deleted } };
        draft = WebsiteContentSanitizer.Sanitize(draft);
        publication = WebsiteContentSanitizer.Sanitize(publication);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = new WebsiteContentState { CommerceBusinessId = business.Id, OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id), SiteKey = WebsiteEditorSiteKeys.Business, DraftJson = JsonSerializer.Serialize(draft, options) };
        db.AddRange(business, state);
        if (hasPublication)
        {
            var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = JsonSerializer.Serialize(publication, options) };
            state.PublishedVersionId = version.Id;
            db.Add(version);
        }
        var retained = new CommerceProduct { CommerceBusinessId = business.Id, Name = "Retained training offer", Slug = "retained-training", BillingKind = ClientSubscriptionScope.MonthlyTraining };
        db.Add(retained);
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Commerce:PublicBaseUrl"] = "https://shopparfait.com", ["Billing:ClientSubscriptions:TrainingSubscriptionsEnabled"] = "true"
        }).Build();
        var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db), new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new HostString("shopparfait.com");
        var billing = new Mock<IBillingOrchestrator>(MockBehavior.Strict);
        var mail = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        var controller = new TrainingSubscriptionsController(stores, new TrainingEnrollmentService(db, billing.Object, mail.Object, config), config) { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.Equal(visible, (await stores.ResolvePublicAsync(context, null))!.TrainingPagePublished);
        var result = await controller.Index(default);
        if (visible) Assert.IsType<ViewResult>(result);
        else
        {
            Assert.IsType<NotFoundResult>(result);
            Assert.IsType<NotFoundResult>(await controller.RequestInvitation(retained.Id, "visitor@example.invalid", null, true, default));
            var legacy = new Protect_Website.Controllers.TrainingPackagesController(stores) { ControllerContext = controller.ControllerContext };
            Assert.IsType<NotFoundResult>(await legacy.Index(default));
            var resources = new TrainingController(stores) { ControllerContext = controller.ControllerContext };
            Assert.IsType<NotFoundResult>(await resources.Index(default));
        }
        context.Request.Headers.Origin = "https://shopparfait.com";
        using var provider = new ServiceCollection().AddSingleton(stores).BuildServiceProvider();
        context.RequestServices = provider;
        var inquiryResolver = new PublicWebsiteRuntimeScopeResolver(db, new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var inquiryScope = await inquiryResolver.ResolveInquiryAsync(context, "/store/training");
        Assert.Equal(visible, inquiryScope is not null && PublicWebsiteRuntimeScopeResolver.IsPublishedPath(inquiryScope, "/store/training"));
        if (!visible)
        {
            context.Items[CommerceSharedHostPreviewGate.OriginalPagePathItem] = "/training";
            var preview = new ParfaitPublicPreviewController(stores, Mock.Of<IWebHostEnvironment>(), config) { ControllerContext = controller.ControllerContext };
            Assert.IsType<NotFoundResult>(await preview.Training(default));
            Assert.IsType<NotFoundResult>(await preview.TrainingPackages(default));
        }
        Assert.Single(db.CommerceProducts);
        Assert.Single(db.Set<WebsiteContentState>());
        billing.VerifyNoOtherCalls(); mail.VerifyNoOtherCalls();
    }
}
