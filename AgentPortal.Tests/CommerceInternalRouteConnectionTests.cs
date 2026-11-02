using System;
using System.Linq;
using System.Threading.Tasks;
using Legend.Commerce;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.Extensions.DependencyInjection;
using ParfaitApp.Controllers;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceInternalRouteConnectionTests
{
    [Theory]
    [InlineData("management")]
    [InlineData("preview")]
    [InlineData("cutover")]
    public async Task Every_internal_adapter_registers_the_existing_action_once(string mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        var mvc = builder.Services.AddControllersWithViews();
        if (mode == "management") mvc.AddLegendCommerceManagementMvc();
        else if (mode == "preview") mvc.AddLegendCommercePreviewMvc();
        else mvc.AddLegendCommerceCutoverMvc();
        await using var app = builder.Build();
        var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Where(x => x.ControllerTypeInfo.AsType() == typeof(CommerceManagementController)).ToArray();
        foreach (var route in CommerceManagementCapabilityPolicy.InternalRoutes)
        {
            bool Matches(ControllerActionDescriptor a, string path) => ("/" + a.AttributeRouteInfo?.Template?.TrimStart('/')).TrimEnd('/') == path.TrimEnd('/') &&
                a.ActionConstraints!.OfType<HttpMethodActionConstraint>().Any(c => c.HttpMethods.Contains(route.Method));
            var original = Assert.Single(actions.Where(a => Matches(a, route.ActionPath)));
            var adapter = Assert.Single(actions.Where(a => Matches(a, route.InternalPath)));
            Assert.Equal(original.MethodInfo, adapter.MethodInfo);
            Assert.Equal(CommerceManagementCapabilityPolicy.ForRequest(route.ActionPath, route.Method),
                CommerceManagementCapabilityPolicy.ForRequest(route.InternalPath, route.Method));
            Assert.NotNull(CommerceManagementCapabilityPolicy.ForRequest(route.InternalPath, route.Method));
        }
        Assert.DoesNotContain(actions, a => a.AttributeRouteInfo?.Template == "/internal/businesses");
    }

    [Fact]
    public async Task Standalone_Parfait_does_not_receive_competing_internal_routes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddLegendCommerceMvc();
        await using var app = builder.Build();
        var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Where(x => x.ControllerTypeInfo.AsType() == typeof(CommerceManagementController));
        Assert.DoesNotContain(actions, a => a.AttributeRouteInfo?.Template?.StartsWith("/internal/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Internal_forms_and_links_keep_scope_and_encode_ticket()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/internal/commerce/products";
        context.Request.QueryString = new QueryString("?businessKey=second-store");
        Assert.Equal("/internal/commerce/products/images/upload?ticket=a%26b&businessKey=second-store",
            CommerceManagementCapabilityPolicy.ManagementUrl(context, "/commerce/manage/product/images/upload", "a&b", "POST"));
        Assert.Equal("/internal/settings/payments?ticket=a%26b&businessKey=second-store",
            CommerceManagementCapabilityPolicy.ManagementUrl(context, "/commerce/manage/payments", "a&b"));
        Assert.Equal("website", CommerceManagementCapabilityPolicy.ForRequest("/internal", "GET"));
        Assert.Equal("website", CommerceManagementCapabilityPolicy.ForRequest("/internal/", "GET"));
        Assert.Throws<InvalidOperationException>(() => CommerceManagementCapabilityPolicy.ManagementUrl(context, "/commerce/manage/not-mapped", "ticket"));
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest("/internal/businesses", "GET"));
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest("/internal/commerce/products", "DELETE"));
    }
}
