using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Infrastructure.Commerce;
using Microsoft.AspNetCore.Hosting;
using Moq;
using ParfaitApp.Services;
using ParfaitApp.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Commerce.Payments;
using Infrastructure.Commerce.Payments.Providers;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class MerchantPaymentsTests
{
    [Fact] public async Task OwnerOnboardingRequiresConfirmationThenCanSelectAndDisconnect()
    {
        using var f=new Fixture();var owner=f.SeedOwner(f.Business,"owner-one");
        var original=(await f.Db.CommerceMerchantConfigurations.SingleAsync()).DefaultConnectionId;
        await f.Connections.BeginAsync(f.Business,owner,"owner-one",null,"Fake","Production","https://untrusted.example/callback","ticket",default);
        Assert.Equal("https://example.test/commerce/manage/payments/callback",f.Gateway.AuthorizationCallback);
        Assert.Equal("ticket",f.Connections.InspectTicket(f.Gateway.AuthorizationState!));
        var id=await f.Connections.CompleteAsync(f.Business,owner,"owner-one",null,f.Gateway.AuthorizationState!,"fake-code",default);
        var row=await f.Db.CommerceMerchantConnections.SingleAsync(x=>x.Id==id);
        Assert.Equal("PendingConfirmation",row.State);Assert.Equal(original,(await f.Db.CommerceMerchantConfigurations.SingleAsync()).DefaultConnectionId);
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Connections.SetDefaultAsync(f.Business,owner,"owner-one",null,id,row.Revision,default));
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Connections.ConfirmAsync(f.Business,owner,"owner-one",null,id,"wrong-merchant",null,row.Revision,default));
        await f.Connections.ConfirmAsync(f.Business,owner,"owner-one",null,id,f.Gateway.ConnectionAccount,null,row.Revision,default);
        await f.Connections.SetDefaultAsync(f.Business,owner,"owner-one",null,id,row.Revision,default);
        Assert.Equal(id,(await f.Connections.ResolveActiveAsync(f.Business,"Production",default))!.Id);
        await f.Connections.RevokeAsync(f.Business,owner,"owner-one",null,id,row.Revision,default);
        Assert.Null(await f.Connections.ResolveActiveAsync(f.Business,"Production",default));Assert.Equal("Disconnected",row.State);
    }
    [Fact] public async Task CallbackIsOneTimeAndRejectsWrongActorAndBusiness()
    {
        using var f=new Fixture();var owner=f.SeedOwner(f.Business,"owner-one");var other=Guid.NewGuid();var otherOwner=f.SeedOwner(other,"owner-two");
        await f.Connections.BeginAsync(f.Business,owner,"owner-one",null,"Fake","Production","ignored","ticket",default);var state=f.Gateway.AuthorizationState!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Connections.CompleteAsync(f.Business,owner,"owner-two",null,state,"fake-code",default));
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Connections.CompleteAsync(other,otherOwner,"owner-two",null,state,"fake-code",default));
        Assert.Equal(0,f.Gateway.AuthorizationCompletions);
        await f.Connections.CompleteAsync(f.Business,owner,"owner-one",null,state,"fake-code",default);
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Connections.CompleteAsync(f.Business,owner,"owner-one",null,state,"fake-code",default));
        Assert.Equal(1,f.Gateway.AuthorizationCompletions);
    }
    [Fact] public async Task MerchantAlreadyBoundToDifferentBusinessCannotBeConnected()
    {
        using var f=new Fixture();var other=Guid.NewGuid();var owner=f.SeedOwner(other,"owner-two");f.Gateway.ConnectionAccount="merchant-original";
        await f.Connections.BeginAsync(other,owner,"owner-two",null,"Fake","Production","ignored","ticket",default);
        var error=await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Connections.CompleteAsync(other,owner,"owner-two",null,f.Gateway.AuthorizationState!,"fake-code",default));
        Assert.Equal("merchant_already_bound",error.Message);
        Assert.Empty(await f.Db.CommerceMerchantConnections.Where(x=>x.CommerceBusinessId==other).ToListAsync());
        Assert.Equal(f.Business,(await f.Db.CommerceMerchantConnections.SingleAsync()).CommerceBusinessId);
    }
    [Fact] public async Task ExternalRefundPlusUnknownReservationBothReduceRefundableAmount()
    {
        using var f=new Fixture();await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        var attempt=await f.Db.CommercePaymentAttempts.SingleAsync();attempt.RefundedMinor=400;
        f.Db.CommercePaymentRefunds.Add(new(){AttemptId=attempt.Id,IdempotencyKey="unknown-earlier",AmountMinor=300,State="Unknown",CreatedUtc=DateTime.UtcNow});await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.RefundAsync(f.Business,f.Order,400,"new-request"));Assert.Equal(0,f.Gateway.Refunds);
    }
    [Fact] public async Task RepeatedCompletionCommitsInventoryPurchaseAndTenantMailOnce()
    {
        using var f=new Fixture(true);
        var root=Path.Combine(Path.GetTempPath(),"merchant-completion-tests",Guid.NewGuid().ToString("N"));
        try
        {
            f.Db.CommerceBusinesses.Add(new(){Id=f.Business,Key="independent",DisplayName="Independent",OwnerEmail="owner@example.test",IsActive=true,Status="Active"});
            f.Db.CommerceProducts.Add(new(){CommerceBusinessId=f.Business,ExternalProductKey="product",Name="Product",Slug="product",IsActive=true,PriceCents=1000,InventoryItems=[new(){ExternalInventoryKey="product-m",Size="M",IsEnabled=true,StockQuantity=5}]});
            f.Db.CommerceOrderLines.Add(new(){CommerceOrderId=f.Order,ProductExternalKey="product",ProductName="Product",ProductSlug="product",Size="M",Quantity=1,UnitPriceCents=1000});
            await f.Db.SaveChangesAsync();
            var context=new CommerceSignalContext(f.Business,null,null,"business","independent","Independent","https://example.test/store",OrderId:f.Order);
            await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return",JsonSerializer.Serialize(context));
            var environment=new Mock<IWebHostEnvironment>();environment.SetupGet(x=>x.ContentRootPath).Returns(root);environment.SetupGet(x=>x.WebRootPath).Returns(root);
            var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Parfait:StorageRoot",root}}).Build();
            var storage=new ParfaitStoragePaths(environment.Object,configuration);var orders=new ParfaitOrderService(f.Db);var products=new ParfaitProductService(storage,f.Db);
            var mail=new Mock<IGraphMailService>();
            mail.Setup(x=>x.SendOrderReceiptAsync(It.IsAny<ParfaitOrderRecord>(),It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mail.Setup(x=>x.SendOrderNotificationAsync(It.IsAny<ParfaitOrderRecord>(),It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var completion=new MerchantPaymentCompletionService(f.Db,orders,products,new ParfaitCustomerAutomationService(storage,configuration,orders,products,f.Db),new CommerceSignalService(f.Db),mail.Object);
            var attempt=await f.Db.CommercePaymentAttempts.SingleAsync();
            await completion.CompleteAsync(attempt.Id);await completion.CompleteAsync(attempt.Id);
            Assert.Equal(4,(await f.Db.CommerceProductInventoryItems.SingleAsync()).StockQuantity);
            Assert.Equal(1,await f.Db.AnalyticsEvents.CountAsync());
            var purchase=await f.Db.AnalyticsEvents.SingleAsync();Assert.Equal(f.Business,purchase.CommerceBusinessId);
            using var payload=JsonDocument.Parse(purchase.MetadataJson!);Assert.Equal(1000,payload.RootElement.GetProperty("valueCents").GetInt32());
            Assert.Equal(5,await f.Db.CommercePaymentEffects.CountAsync(x=>x.State=="Completed"));
            mail.Verify(x=>x.SendOrderReceiptAsync(It.Is<ParfaitOrderRecord>(o=>o.CommerceBusinessId==f.Business),It.IsAny<CancellationToken>()),Times.Once);
            mail.Verify(x=>x.SendOrderNotificationAsync(It.Is<ParfaitOrderRecord>(o=>o.CommerceBusinessId==f.Business),It.IsAny<CancellationToken>()),Times.Once);
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TwoTenantsRouteToTheirOwnMerchant(bool differentProvider)
    {
        using var f=new Fixture();var business=Guid.NewGuid();var order=Guid.NewGuid();
        var provider=differentProvider?"Other":"Fake";
        f.SeedTenant(business,order,provider,"merchant-second");
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        Assert.Equal("merchant-original",f.Gateway.LastAccount);
        await f.Checkout.BeginAsync(business,order,"https://example.test/return");
        Assert.Equal("merchant-second",(differentProvider?f.OtherGateway:f.Gateway).LastAccount);
        Assert.Equal(2,await f.Db.CommercePaymentAttempts.CountAsync());
    }
    [Fact] public async Task SandboxCheckoutNeverUsesProductionConnection()
    {
        using var f=new Fixture();
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"MerchantPayments:CheckoutEnvironment","Sandbox"}}).Build();
        var checkout=new MerchantCheckoutService(f.Db,f.Connections,new[]{f.Gateway},configuration);
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>checkout.BeginAsync(f.Business,f.Order,"https://example.test/return"));
        Assert.Equal(0,f.Gateway.Creates);
    }
    [Fact] public async Task OutOfOrderPendingEvidenceCannotReverseCapturedState()
    {
        using var f=new Fixture();
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        f.Gateway.Pending=true;
        Assert.Equal("Captured",(await f.Checkout.ReconcileAsync(f.Business,f.Order))!.State);
    }
    [Fact] public async Task IncorrectSquareLocationCannotCharge()
    {
        using var f=new Fixture();f.OtherGateway.Name="Square";f.OtherGateway.Locations=new[]{"verified-location"};
        var business=Guid.NewGuid();var order=Guid.NewGuid();f.SeedTenant(business,order,"Square","merchant-square");
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.BeginAsync(business,order,"https://example.test/return"));
        Assert.Equal(0,f.OtherGateway.Creates);
    }
    [Fact] public async Task DuplicateSubmissionCallsProviderOnce()
    {
        using var f = new Fixture();
        await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return");
        await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return");
        Assert.Equal(1, f.Gateway.Creates);
    }
    [Fact] public async Task UnknownCreateOutcomeNeverResubmits()
    {
        using var f = new Fixture(); f.Gateway.ThrowCreate = true;
        Assert.Equal("Unknown", (await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return")).State);
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        await f.Checkout.ReconcileAsync(f.Business,f.Order);
        Assert.Equal(1,f.Gateway.Creates); Assert.Equal(0,f.Gateway.Reads);
    }
    [Fact] public async Task CrossTenantOrderCannotCharge()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.BeginAsync(Guid.NewGuid(),f.Order,"https://example.test/return"));
        Assert.Equal(0,f.Gateway.Creates);
    }
    [Theory] [InlineData("amount")] [InlineData("environment")] [InlineData("account")]
    public async Task MismatchedEvidenceNeverCaptures(string mismatch)
    {
        using var f = new Fixture(); f.Gateway.Mismatch=mismatch;
        var result=await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        Assert.Equal("Unknown",result.State);
        Assert.Null((await f.Db.CommerceOrders.FindAsync(f.Order))!.PaidUtc);
    }
    [Fact] public async Task HistoricalReconciliationAndRefundUsePinnedConnection()
    {
        using var f=new Fixture();
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        f.SeedTenant(f.Business,Guid.NewGuid(),"Other","merchant-new-provider");
        await f.Checkout.ReconcileAsync(f.Business,f.Order);
        await f.Checkout.RefundAsync(f.Business,f.Order,100,"refund-request");
        Assert.Equal("merchant-original",f.Gateway.LastAccount);
        Assert.Equal(1,f.Gateway.Refunds); Assert.Equal(0,f.OtherGateway.Refunds); Assert.Equal(0,f.OtherGateway.Reads);
        await f.Checkout.RefundAsync(f.Business,f.Order,100,"refund-request");
        Assert.Equal(1,f.Gateway.Refunds);
    }
    [Theory] [InlineData("missing")] [InlineData("disconnected")] [InlineData("unavailable")] [InlineData("not-ready")]
    public async Task RoutingFailsClosed(string scenario)
    {
        using var f=new Fixture();
        if(scenario=="missing") f.Db.CommerceMerchantConfigurations.Remove(await f.Db.CommerceMerchantConfigurations.SingleAsync());
        if(scenario=="disconnected") (await f.Db.CommerceMerchantConnections.SingleAsync()).State="Disconnected";
        if(scenario=="unavailable") f.Gateway.Available=false;
        if(scenario=="not-ready") f.Gateway.Ready=false;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return"));
        Assert.Equal(0,f.Gateway.Creates);
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task ConcurrentReconciliationClaimsOnlyOneCapture(bool relational)
    {
        using var f=new Fixture(relational); f.Gateway.Approved=true;
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        f.Gateway.SynchronizeReads=true;
        using var db1=new MasterAppDbContext(f.Options); using var db2=new MasterAppDbContext(f.Options);
        MerchantCheckoutService Service(MasterAppDbContext db)=>new(db,new MerchantConnectionService(db,f.Protection,new[]{f.Gateway},new ConfigurationBuilder().Build()),new[]{f.Gateway},new ConfigurationBuilder().Build());
        async Task Reconcile(MerchantCheckoutService service)
        {
            try { await service.ReconcileAsync(f.Business,f.Order); }
            catch(DbUpdateConcurrencyException) { }
        }
        await Task.WhenAll(Reconcile(Service(db1)),Reconcile(Service(db2)));
        Assert.Equal(1,f.Gateway.Captures);
    }
    [Fact] public async Task SqliteRejectsSecondPaymentAttemptForSameOrder()
    {
        using var f=new Fixture(true);
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        var original=await f.Db.CommercePaymentAttempts.AsNoTracking().SingleAsync();
        original.Id=Guid.NewGuid();original.IdempotencyKey="different-key";
        f.Db.CommercePaymentAttempts.Add(original);
        await Assert.ThrowsAsync<DbUpdateException>(()=>f.Db.SaveChangesAsync());
    }
    [Fact] public async Task AmbiguousCaptureIsNeverSubmittedAgain()
    {
        using var f=new Fixture(); f.Gateway.Approved=true;
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        Assert.Equal("Unknown",(await f.Checkout.ReconcileAsync(f.Business,f.Order))!.State);
        await f.Checkout.ReconcileAsync(f.Business,f.Order);
        Assert.Equal(1,f.Gateway.Captures);
        Assert.NotNull((await f.Db.CommercePaymentAttempts.SingleAsync()).CaptureRequestedUtc);
    }
    [Fact] public async Task UnknownRefundNeverResubmitsAndReservesAmount()
    {
        using var f=new Fixture();
        await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        f.Gateway.ThrowRefund=true;
        Assert.Equal("Unknown",(await f.Checkout.RefundAsync(f.Business,f.Order,600,"ambiguous-refund")).State);
        await f.Checkout.RefundAsync(f.Business,f.Order,600,"ambiguous-refund");
        Assert.Equal(1,f.Gateway.Refunds);
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.RefundAsync(f.Business,f.Order,500,"different-refund"));
        Assert.Equal(1,f.Gateway.Refunds);
    }
    [Fact] public async Task ForgedWebhookRejectedAndDuplicateEventReconcilesOnce()
    {
        using var f=new Fixture(); await f.Checkout.BeginAsync(f.Business,f.Order,"https://example.test/return");
        await Assert.ThrowsAsync<MerchantPaymentException>(()=>f.Checkout.HandleWebhookAsync("Fake","Production","forged",new Dictionary<string,string>(),"https://example.test/hook"));
        await f.Checkout.HandleWebhookAsync("Fake","Production","signed",new Dictionary<string,string>(),"https://example.test/hook");
        await f.Checkout.HandleWebhookAsync("Fake","Production","signed",new Dictionary<string,string>(),"https://example.test/hook");
        Assert.Equal(1,f.Gateway.Reads);
    }
    [Fact] public void TamperedAuthorizationStateRejected()
    {
        using var f=new Fixture();
        Assert.Throws<MerchantPaymentException>(()=>f.Connections.InspectTicket("forged-state"));
    }
    [Fact] public void RealSignatureAlgorithmsRejectTamperingAndStaleStripeEvents()
    {
        const string body="{\"id\":\"event_test\"}"; const string key="nonsecret-test-key"; const string url="https://example.test/hook";
        var square=Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(url+body)));
        Assert.True(PaymentWebhookSignatures.VerifySquare(body,square,key,url));
        Assert.False(PaymentWebhookSignatures.VerifySquare(body+" ",square,key,url));
        var now=DateTimeOffset.UtcNow; var timestamp=now.ToUnixTimeSeconds();
        var stripe="t="+timestamp+",v1="+Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(timestamp+"."+body)));
        Assert.True(PaymentWebhookSignatures.VerifyStripe(body,stripe,key,now));
        Assert.False(PaymentWebhookSignatures.VerifyStripe(body+" ",stripe,key,now));
        Assert.False(PaymentWebhookSignatures.VerifyStripe(body,stripe,key,now.AddMinutes(6)));
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(null, null)]
    public async Task SquarePaymentEligibilityDoesNotRequireAutomaticTransfers(bool? payments, bool? transfers)
    {
        using var f = new Fixture(provider: "Square");
        var owner = f.SeedOwner(f.Business, "owner-one");
        var row = await f.Db.CommerceMerchantConnections.SingleAsync();
        row.LocationId = "verified-location";
        (await f.Db.CommerceMerchantConnectionRevisions.SingleAsync()).LocationId = row.LocationId;
        await f.Db.SaveChangesAsync();
        f.Gateway.ReadinessOverride = new(payments, null, ["USD"], ["card"], [row.LocationId], AutomaticTransfersEnabled: transfers);
        if (payments == true)
        {
            await f.Connections.SetDefaultAsync(f.Business, owner, "owner-one", null, row.Id, row.Revision, default);
            var payment = await f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return");
            Assert.Equal("Captured", payment.State); // Synthetic payment evidence, not a bank payout.
            Assert.Equal(1, f.Gateway.Creates);
        }
        else
        {
            await Assert.ThrowsAsync<MerchantPaymentException>(() => f.Connections.SetDefaultAsync(f.Business, owner, "owner-one", null, row.Id, row.Revision, default));
            await Assert.ThrowsAsync<MerchantPaymentException>(() => f.Checkout.BeginAsync(f.Business, f.Order, "https://example.test/return"));
            Assert.Equal(0, f.Gateway.Creates);
        }
    }

    [Theory]
    [InlineData("{\"PaymentsEnabled\":true,\"SettlementsEnabled\":true,\"Currencies\":[\"USD\"],\"PaymentMethods\":[\"card\"],\"Locations\":[\"location\"]}", true)]
    [InlineData("{}", null)]
    [InlineData("null", null)]
    [InlineData("not json", null)]
    [InlineData("{\"PaymentsEnabled\":\"yes\"}", null)]
    [InlineData("{\"PaymentsEnabled\":true,\"Currencies\":null}", null)]
    public async Task CachedSquareReadinessCannotFabricateSettlements(string json, bool? expectedPayments)
    {
        using var f = new Fixture(provider: "Square");
        var owner = f.SeedOwner(f.Business, "owner-one");
        (await f.Db.CommerceMerchantConnections.SingleAsync()).ReadinessJson = json;
        await f.Db.SaveChangesAsync();
        var readiness = (await f.Connections.ListAsync(f.Business, owner, "owner-one", null, "Production", default)).Connections.Single().Readiness;
        Assert.Equal(expectedPayments, readiness.PaymentsEnabled);
        Assert.Null(readiness.SettlementsEnabled);
        Assert.Null(readiness.AutomaticTransfersEnabled);
        Assert.NotNull(readiness.Currencies); Assert.NotNull(readiness.Locations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedExplicitAutomaticTransferCapabilityIsRetainedWithoutSettlementClaim(bool transfers)
    {
        using var f = new Fixture(provider: "Square");
        var owner = f.SeedOwner(f.Business, "owner-one");
        (await f.Db.CommerceMerchantConnections.SingleAsync()).ReadinessJson = JsonSerializer.Serialize(
            new MerchantReadiness(true, true, ["USD"], ["card"], ["location"], AutomaticTransfersEnabled: transfers));
        await f.Db.SaveChangesAsync();
        var readiness = (await f.Connections.ListAsync(f.Business, owner, "owner-one", null, "Production", default)).Connections.Single().Readiness;
        Assert.Equal(transfers, readiness.AutomaticTransfersEnabled);
        Assert.Null(readiness.SettlementsEnabled);
    }

    private sealed class Fixture:IDisposable
    {
        public readonly DbContextOptions<MasterAppDbContext> Options; public readonly IDataProtectionProvider Protection=new EphemeralDataProtectionProvider(); public readonly MasterAppDbContext Db; public readonly FakeGateway Gateway,OtherGateway=new("Other"); public readonly Guid Business=Guid.NewGuid(),Order=Guid.NewGuid();
        public readonly MerchantConnectionService Connections; public readonly MerchantCheckoutService Checkout;
        private readonly SqliteConnection? sqlite;
        public Fixture(bool relational=false, string provider="Fake")
        {
            Gateway = new(provider);
            if(relational)
            {
                sqlite=new SqliteConnection("Data Source=:memory:;Foreign Keys=False");sqlite.Open();
                Options=new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options;
            }
            else Options=new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x=>x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
            Db=new(Options);if(relational)Db.Database.EnsureCreated();
            var protection=Protection; var id=Guid.NewGuid(); var revision=Guid.NewGuid();
            var protectedCredential=protection.CreateProtector("Legend.Merchant.Credential.v1",id.ToString("N"),Business.ToString("N"),provider,"Production").Protect(JsonSerializer.Serialize(new MerchantCredential("test-only")));
            Db.CommerceOrders.Add(new() {Id=Order,CommerceBusinessId=Business,TotalCents=1000,OrderNumber="TEST"});
            Db.CommerceMerchantConnections.Add(new() {Id=id,CommerceBusinessId=Business,Provider=provider,Environment="Production",AccountId="merchant-original",Revision=1,State="Active",ProtectedCredential=protectedCredential});
            Db.CommerceMerchantConnectionRevisions.Add(new() {Id=revision,ConnectionId=id,CommerceBusinessId=Business,Provider=provider,Environment="Production",AccountId="merchant-original",Revision=1});
            Db.CommerceMerchantConfigurations.Add(new() {CommerceBusinessId=Business,Environment="Production",DefaultConnectionId=id}); Db.SaveChanges();
            Connections=new(Db,protection,new IMerchantPaymentGateway[]{Gateway,OtherGateway},new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"MerchantPayments:"+provider+":Production:CallbackUrl","https://example.test/commerce/manage/payments/callback"}}).Build()); Checkout=new(Db,Connections,new IMerchantPaymentGateway[]{Gateway,OtherGateway},new ConfigurationBuilder().Build());
        }
        public Guid SeedOwner(Guid business,string actor)
        {
            if(!Db.CommerceBusinesses.Any(x=>x.Id==business))Db.CommerceBusinesses.Add(new(){Id=business,Key="business-"+business.ToString("N"),DisplayName="Business",IsActive=true,Status="Active"});
            var profile=new ClientProfile{ClientUserId=actor,Email=actor+"@example.test"};Db.ClientProfiles.Add(profile);
            Db.CommerceBusinessMembers.Add(new(){CommerceBusinessId=business,ClientProfileId=profile.Id,RoleKey="owner",Status="Active",CanManageStorefront=true});Db.SaveChanges();return profile.Id;
        }
        public void SeedTenant(Guid business,Guid order,string provider,string account)
        {
            var id=Guid.NewGuid();var revision=Guid.NewGuid();
            var credential=Protection.CreateProtector("Legend.Merchant.Credential.v1",id.ToString("N"),business.ToString("N"),provider,"Production").Protect(JsonSerializer.Serialize(new MerchantCredential("test-only")));
            Db.CommerceOrders.Add(new(){Id=order,CommerceBusinessId=business,TotalCents=1000,OrderNumber="TEST-SECOND"});
            Db.CommerceMerchantConnections.Add(new(){Id=id,CommerceBusinessId=business,Provider=provider,Environment="Production",AccountId=account,Revision=1,State="Active",ProtectedCredential=credential});
            Db.CommerceMerchantConnectionRevisions.Add(new(){Id=revision,ConnectionId=id,CommerceBusinessId=business,Provider=provider,Environment="Production",AccountId=account,Revision=1});
            var configuration=Db.CommerceMerchantConfigurations.Local.FirstOrDefault(x=>x.CommerceBusinessId==business);
            if(configuration is null)Db.CommerceMerchantConfigurations.Add(new(){CommerceBusinessId=business,Environment="Production",DefaultConnectionId=id});else configuration.DefaultConnectionId=id;Db.SaveChanges();
        }
        public void Dispose(){Db.Dispose();sqlite?.Dispose();}
    }
    private sealed class FakeGateway(string provider="Fake"):IMerchantPaymentGateway
    {
        public string Name=provider; public string Provider=>Name; public string[] Locations=Array.Empty<string>(); public MerchantCapabilities Capabilities=>MerchantCapabilities.Checkout|MerchantCapabilities.Refund|MerchantCapabilities.Reconcile|MerchantCapabilities.Capture;
        public int Creates,Reads,Refunds,Captures; public bool Available=true,Ready=true,Approved,Pending,SynchronizeReads; private readonly TaskCompletionSource<bool> readsReady=new(TaskCreationOptions.RunContinuationsAsynchronously); public bool ThrowCreate,ThrowRefund; public string? Mismatch,LastAccount;
        public MerchantReadiness? ReadinessOverride;
        public string? AuthorizationState,AuthorizationCallback; public string ConnectionAccount="merchant-new";public int AuthorizationCompletions;
        public bool IsAvailable(string environment)=>Available;
        public Task<MerchantAuthorization> BeginConnectionAsync(MerchantConnectionRequest r,CancellationToken ct){AuthorizationState=r.State;AuthorizationCallback=r.CallbackUrl;return Task.FromResult(new MerchantAuthorization("https://provider.example.test/authorize",ConnectionAccount));}
        public Task<MerchantConnectionResult> CompleteConnectionAsync(string e,string c,string u,string? a,CancellationToken ct){AuthorizationCompletions++;return Task.FromResult(new MerchantConnectionResult(ConnectionAccount,new MerchantCredential("test-only"),new[]{"checkout"}));}
        public Task<MerchantReadiness> GetReadinessAsync(MerchantAccount a,CancellationToken ct)=>Task.FromResult(ReadinessOverride ?? new MerchantReadiness(Ready,Ready,new[]{"USD"},new[]{"card"},Locations));
        public Task<MerchantCredential> RefreshAsync(MerchantAccount a,CancellationToken ct)=>Task.FromResult(a.Credential);
        public Task RevokeAsync(MerchantAccount a,CancellationToken ct)=>Task.CompletedTask;
        private MerchantPaymentEvidence Evidence(MerchantAccount a)=>new(Mismatch=="account"?"wrong":a.AccountId,Mismatch=="environment"?"Sandbox":a.Environment,"checkout_test","payment_test",Approved?"Approved":Pending?"Pending":"Captured",Mismatch=="amount"?999:1000,"USD");
        public Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount a,MerchantCheckoutRequest r,CancellationToken ct) {Creates++; LastAccount=a.AccountId; if(ThrowCreate)throw new TimeoutException(); return Task.FromResult(Evidence(a));}
        public async Task<MerchantPaymentEvidence> GetPaymentAsync(MerchantAccount a,string r,CancellationToken ct) {var reads=Interlocked.Increment(ref Reads);LastAccount=a.AccountId;if(SynchronizeReads) {if(reads==2)readsReady.TrySetResult(true);await readsReady.Task.WaitAsync(TimeSpan.FromSeconds(10),ct);}return Evidence(a);}
        public Task<MerchantPaymentEvidence> CaptureAsync(MerchantAccount a,string r,string key,CancellationToken ct) {Captures++;throw new TimeoutException();}
        public Task<MerchantRefundResult> RefundAsync(MerchantAccount a,MerchantRefundRequest r,CancellationToken ct) {Refunds++;LastAccount=a.AccountId;if(ThrowRefund)throw new TimeoutException();return Task.FromResult(new MerchantRefundResult("refund_test","Succeeded",r.PaymentReference,r.AmountMinor,r.Currency));}
        public Task<MerchantWebhook?> VerifyWebhookAsync(string e,string b,IReadOnlyDictionary<string,string> h,string u,CancellationToken ct)=>Task.FromResult<MerchantWebhook?>(b=="signed"?new("event_test","merchant-original",e,"checkout_test",DateTime.UtcNow):null);
    }
}
