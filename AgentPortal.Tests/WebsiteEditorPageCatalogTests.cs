using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Moq;
using ProtectWebsite.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteEditorPageCatalogTests
{
    private sealed class NewPublicPageController : Controller { }

    [Fact]
    public void DiscoversNewViewBackedGetPageWithoutAddingItToEditorPageList()
    {
        var action = new ControllerActionDescriptor
        {
            ControllerName = "NewPublicPage", ActionName = "Index",
            ControllerTypeInfo = typeof(NewPublicPageController).GetTypeInfo(),
            ActionConstraints = [new HttpMethodActionConstraint(["GET"])]
        };
        var actions = new Mock<IActionDescriptorCollectionProvider>();
        actions.SetupGet(x => x.ActionDescriptors).Returns(new ActionDescriptorCollection([action], 1));
        var views = new Mock<IRazorViewEngine>();
        views.Setup(x => x.GetView(null, "~/Views/NewPublicPage/Index.cshtml", false))
            .Returns(ViewEngineResult.Found("~/Views/NewPublicPage/Index.cshtml", Mock.Of<IView>()));
        var catalog = WebsiteEditorPageCatalog.Discover(actions.Object, views.Object);
        Assert.Contains(catalog, p => p.Path == "/NewPublicPage");
        Assert.Equal(catalog.Count, catalog.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        action.ActionConstraints = [new HttpMethodActionConstraint(["POST"])];
        Assert.DoesNotContain(WebsiteEditorPageCatalog.Discover(actions.Object, views.Object), p => p.Path == "/NewPublicPage");
    }
}
