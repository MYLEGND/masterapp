using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;
namespace AgentPortal.Tests;
public sealed class CommerceRetainedTransactionTests
{
    [Theory]
    [InlineData("disabled", false)]
    [InlineData("unpublished", false)]
    [InlineData("deleted", false)]
    [InlineData("unpublished", true)]
    public async Task HiddenStoreRetainsOnlyScopedExistingProcessorTransactions(string hidden, bool legacy)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key="retained-store", DisplayName="Retained", IsActive=true, Status="Active" };
        var document = new WebsiteContentDocument { Store = new() { Enabled=hidden!="disabled" } };
        WebsiteSystemTemplateAuthority.AddStorePage(document);
        document.Pages["/store"].Navigation.IsPublished = hidden!="unpublished";
        document.Pages["/store"].Navigation.IsDeleted = hidden=="deleted";
        var state = new WebsiteContentState { SiteKey=WebsiteEditorSiteKeys.Legend, OwnerKey=WebsiteEditorSiteKeys.GlobalOwnerKey, CommerceBusinessId=business.Id };
        var version = new WebsiteContentVersion { StateId=state.Id, DocumentJson=JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(document), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        state.PublishedVersionId=version.Id;
        var order = new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="RETAINED-1", CheckoutAttemptId="opaque-attempt" };
        db.AddRange(business,state,version,order);
        if(legacy) db.Add(new SubscriptionPayment { CommerceOrderId=order.Id, Kind=Domain.Billing.SubscriptionPaymentKind.CommerceOneTime });
        else db.Add(new CommercePaymentAttempt { CommerceBusinessId=business.Id, CommerceOrderId=order.Id });
        await db.SaveChangesAsync();
        var secret=new string('x',40);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["WebsiteRouting:BridgeSecret"]=secret,["WebsiteRouting:BridgeOriginHost"]="masterapp-parfait.azurewebsites.net" }).Build();
        var service=new CommerceStoreContextService(db,new CommerceBusinessScopeResolver(db),new ParfaitBusinessScopeService(db),new WebsiteDomainService(db,Mock.Of<IHttpClientFactory>(),config),config);
        var http=new DefaultHttpContext();http.Request.Host=new HostString("masterapp-parfait.azurewebsites.net");
        http.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader]="mylegnd.com";
        http.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader]=secret;
        Assert.Null(await service.ResolvePublicAsync(http,null));
        Assert.NotNull(await service.ResolveRetainedTransactionAsync(http,null,order.OrderNumber,"opaque-attempt"));
        Assert.NotNull(await service.ResolveRetainedTransactionAsync(http,null,null,"opaque-attempt"));
        var duplicate=new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="DUPLICATE", CheckoutAttemptId="opaque-attempt" };
        db.Add(duplicate);await db.SaveChangesAsync();
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,null,"opaque-attempt"));
        db.Remove(duplicate);await db.SaveChangesAsync();
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,order.OrderNumber,null));
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,order.OrderNumber,"wrong"));
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,"foreign-order","opaque-attempt"));
        var unpaid=new CommerceOrder { CommerceBusinessId=business.Id, OrderNumber="NO-PROCESSOR", CheckoutAttemptId="other-attempt", PaymentStatus="Paid" };
        db.Add(unpaid);await db.SaveChangesAsync();
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,unpaid.OrderNumber,"other-attempt"));
        Assert.Null(await service.ResolveRetainedPaidReceiptAsync(http,null,order.OrderNumber));
        order.PaymentStatus="Paid";
        if(legacy) { foreach(var payment in db.SubscriptionPayments) payment.Status=Domain.Billing.SubscriptionPaymentStatus.Completed; }
        else { foreach(var payment in db.CommercePaymentAttempts) payment.State="Captured"; }
        await db.SaveChangesAsync();
        Assert.NotNull(await service.ResolveRetainedPaidReceiptAsync(http,null,order.OrderNumber));
        Assert.Null(await service.ResolveRetainedPaidReceiptAsync(http,null,"foreign-order"));
        Assert.Null(await service.ResolveRetainedPaidReceiptAsync(http,null,unpaid.OrderNumber));
        business.IsActive=false;await db.SaveChangesAsync();
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,order.OrderNumber,"opaque-attempt"));
        business.IsActive=true;state.PublishedVersionId=null;await db.SaveChangesAsync();
        Assert.Null(await service.ResolveRetainedTransactionAsync(http,null,order.OrderNumber,"opaque-attempt"));
    }
}
