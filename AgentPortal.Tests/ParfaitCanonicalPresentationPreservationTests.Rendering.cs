using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParfaitApp.Models;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ParfaitCanonicalPresentationPreservationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StoreIndexExecutesCanonicalCatalogPartial(bool authored, bool hasPhoto)
    {
        using var host = await new HostBuilder().ConfigureWebHost(builder => builder
            .UseEnvironment("Testing").UseTestServer()
            .ConfigureServices(services => services.AddControllersWithViews()
                .AddApplicationPart(Assembly.Load("ParfaitApp")))
            .Configure(app => { })).StartAsync();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = services };
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var found = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "~/Views/Store/Index.cshtml", false);
        Assert.True(found.Success);
        var data = new ViewDataDictionary<ParfaitStorefrontViewModel>(
            services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = new() { StoreName = "Own Store", Headline = "", Subheadline = "",
                Products = [new() { Id = "own-product", Name = "Own catalog product",
                    Slug = "own-product", Description = "", PriceLabel = "$25.00",
                    PriceCents = 2500, DisplayPriceCents = 2500,
                    Images = hasPhoto ? [new() { Id = "own-photo", ImageUrl = "/own-photo.jpg" }] : [] }] }
        };
        data["StoreProductBasePath"] = "/store/s/own-business/product";
        if (authored) data["CommerceStoreContext"] = Context(false, null, null) with {
            StorePageBeforeCatalog = "<section data-authored=\"true\">", StorePageAfterCatalog = "</section>" };
        using var writer = new StringWriter();
        await found.View.RenderAsync(new ViewContext(context, found.View, data,
            new TempDataDictionary(http, services.GetRequiredService<ITempDataProvider>()),
            writer, new HtmlHelperOptions()));
        var html = writer.ToString();
        Assert.Contains("Own catalog product", html);
        Assert.Contains("$25.00", html);
        Assert.Contains("/store/s/own-business/product/own-product", html);
        Assert.DoesNotContain("<partial", html);
        Assert.DoesNotContain("parfait-logo", html);
        Assert.DoesNotContain("src=\"\"", html);
        Assert.Equal(hasPhoto, html.Contains("/own-photo.jpg"));
        Assert.Equal(!hasPhoto, html.Contains("No product photo"));
        Assert.Equal(authored, html.Contains("data-authored"));
    }

}
