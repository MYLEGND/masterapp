using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing.Square;
using Infrastructure.Businesses;
using Infrastructure.Commerce;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Legend.Commerce;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceRetainedTransactionHttpTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("unpublished")]
    [InlineData("deleted")]
    public async Task HiddenStoreHttpRetainsReceiptsAndReadOnlyStatusButRejectsNewShopping(string hidden)
    {
        // A private relational database per case; never the browser fixture or a provider endpoint.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key="retained-http", DisplayName="Retained", IsActive=true, Status="Active" };
        var document = new WebsiteContentDocument { Store=new() { Enabled=hidden!="disabled" } };
        WebsiteSystemTemplateAuthority.AddStorePage(document);
        document.Pages["/store"].Navigation.IsPublished=hidden!="unpublished";
        document.Pages["/store"].Navigation.IsDeleted=hidden=="deleted";
        var state = new WebsiteContentState { SiteKey=WebsiteEditorSiteKeys.Legend, OwnerKey=WebsiteEditorSiteKeys.GlobalOwnerKey, CommerceBusinessId=business.Id };
        var version = new WebsiteContentVersion { StateId=state.Id, DocumentJson=JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(document),new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId=version.Id;
        var paid = new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="PAID-RETAINED", CheckoutAttemptId="paid-opaque", PaymentStatus="Paid", PaidUtc=DateTime.UtcNow, TotalCents=1000 };
        var pending = new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="PENDING-RETAINED", CheckoutAttemptId="pending-opaque", PaymentStatus="Pending", TotalCents=1000 };
        var unproven = new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="UNPROVEN", CheckoutAttemptId="unproven-opaque", PaymentStatus="Paid" };
        var foreign = new CommerceOrder { CommerceBusinessId=Guid.NewGuid(), OrderNumber="FOREIGN", CheckoutAttemptId="foreign-opaque", PaymentStatus="Paid" };
        db.AddRange(business,state,version,paid,pending,unproven,foreign,
            new CommercePaymentAttempt { CommerceBusinessId=business.Id, CommerceOrderId=paid.Id, State="Captured", AmountMinor=1000 },
            new SubscriptionPayment { CommerceOrderId=pending.Id, Kind=SubscriptionPaymentKind.CommerceOneTime, Status=SubscriptionPaymentStatus.Pending, AmountCents=1000 });
        await db.SaveChangesAsync();
        var secret=new string('x',40);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["WebsiteRouting:BridgeSecret"]=secret,["WebsiteRouting:BridgeOriginHost"]="masterapp-parfait.azurewebsites.net" }).Build();
        var mail=new Mock<IGraphMailService>(MockBehavior.Strict);
        var billing=new Mock<IBillingOrchestrator>(MockBehavior.Strict);
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseEnvironment("Testing").UseContentRoot(System.IO.Path.GetTempPath()).UseTestServer().ConfigureServices(services=> {
            services.AddSingleton<IConfiguration>(config); services.AddSingleton(db);
            services.AddDataProtection(); services.AddHttpClient(); services.AddHttpContextAccessor();
            services.AddSingleton(new SquareBillingOptions()); services.AddSingleton(mail.Object); services.AddSingleton(billing.Object);
            services.AddLegendCommerceCore();
            // No gateway registered: these retained reads must not create/capture any payment.
            services.RemoveAll<IMerchantPaymentGateway>();
            services.AddScoped<CommerceBusinessScopeResolver>(); services.AddScoped<ParfaitBusinessScopeService>();
            services.AddScoped<WebsiteDomainService>(); services.AddScoped<CommerceStoreContextService>();
            services.AddAntiforgery(o=>o.HeaderName="RequestVerificationToken");
            services.AddControllersWithViews(o=>o.Filters.Add<ReceiptModelResult>()).AddApplicationPart(typeof(StoreCheckoutController).Assembly);
        }).Configure(app=> {
            app.UseRouting();
            app.UseEndpoints(endpoints=> {
                endpoints.MapControllers();
                endpoints.MapGet("/fixture-token",async context=> {
                    var token=context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
                    await context.Response.WriteAsJsonAsync(new { token=token.RequestToken });
                });
            });
        })).StartAsync();
        using var client=host.GetTestClient(); client.BaseAddress=new Uri("https://masterapp-parfait.azurewebsites.net");
        client.DefaultRequestHeaders.Add(WebsiteRequestHostResolver.OriginalHostHeader,"mylegnd.com");
        client.DefaultRequestHeaders.Add(WebsiteRequestHostResolver.BridgeSecretHeader,secret);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        foreach(var path in new[]{"/store","/store/cart","/store/product/anything","/store/checkout"})
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(path)).StatusCode);
        var tokenResponse=await client.GetAsync("/fixture-token");
        client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",tokenResponse.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
        var token=await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add("RequestVerificationToken",token.GetProperty("token").GetString());
        foreach(var path in new[]{"/store/checkout/quote","/store/checkout/pay"})
            Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsJsonAsync(path,new {})).StatusCode);
        var receipt=await client.GetAsync("/store/success?orderNumber=PAID-RETAINED");
        Assert.Equal(HttpStatusCode.OK,receipt.StatusCode);
        Assert.Equal("PAID-RETAINED",(await receipt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderNumber").GetString());
        Assert.Contains("no-store",receipt.Headers.CacheControl!.ToString());
        var status=await client.GetAsync("/store/checkout/return?attempt=pending-opaque");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,status.StatusCode);
        Assert.True((await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentPending").GetBoolean());
        var pendingReceipt=await client.GetAsync("/store/success?orderNumber=PENDING-RETAINED&attempt=pending-opaque");
        Assert.Equal(HttpStatusCode.OK,pendingReceipt.StatusCode);
        Assert.Contains("not been confirmed",await pendingReceipt.Content.ReadAsStringAsync());
        foreach(var path in new[]{"/store/checkout/return?attempt=wrong", "/store/success?orderNumber=FOREIGN&attempt=foreign-opaque", "/store/success?orderNumber=UNPROVEN"})
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(path)).StatusCode);
        Assert.Equal(4,await db.CommerceOrders.CountAsync()); Assert.Single(await db.CommercePaymentAttempts.ToListAsync());
        Assert.Equal(SubscriptionPaymentStatus.Pending,(await db.SubscriptionPayments.SingleAsync()).Status);
        Assert.Empty(mail.Invocations); Assert.Empty(billing.Invocations);
        business.IsActive=false; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/store/success?orderNumber=PAID-RETAINED")).StatusCode);
    }

    // Exercise real routing, resolver and controller authorization. Rendering is separately
    // browser-tested; expose only the authorized receipt model instead of loading host Razor.
    private sealed class ReceiptModelResult : IAsyncResultFilter
    {
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if(context.Result is ViewResult { Model: ParfaitOrderSuccessViewModel model })
                context.Result=new JsonResult(new { orderNumber=model.Order?.OrderNumber });
            await next();
        }
    }
}
