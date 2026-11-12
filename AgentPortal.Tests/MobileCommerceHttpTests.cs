using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileCommerceHttpTests
{
    [Fact]
    public async Task PortalRoutesPreserveScopedCanonicalFulfillmentAndExcludeStorefrontControllers()
    {
        var sql = Environment.GetEnvironmentVariable("LEGEND_MOBILE_TEST_SQL");
        await using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        var options = new DbContextOptionsBuilder<MasterAppDbContext>();
        if (sql is null) { await sqlite.OpenAsync(); options.UseSqlite(sqlite); }
        else
        {
            var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql);
            Assert.StartsWith("127.0.0.1,", parsed.DataSource);
            Assert.StartsWith("LegendRehearsal_", parsed.InitialCatalog);
            options.UseSqlServer(sql);
        }
        await using var db = new MasterAppDbContext(options.Options);
        await db.Database.EnsureCreatedAsync();
        var profile = new ClientProfile { ClientUserId = "http-fixture-owner", Email = "fixture@example.invalid" };
        var business = new CommerceBusiness { Key = "http-fixture", DisplayName = "Fixture" };
        var revision = DateTime.Parse("2026-11-03T10:20:30.1234567Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var order = new CommerceOrder { CommerceBusinessId = business.Id, OrderNumber = "HTTP-FIXTURE", PaymentStatus = "Paid", TotalCents = 1000,
            Email = "fixture@example.invalid", UpdatedUtc = revision, PaymentReference = "retained-provider-reference" };
        db.AddRange(profile, business, order, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, CanManageCrm = true },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id },
            new WorkstationLeadProfile { LeadId = "http-work-lead", CommerceBusinessId = business.Id, AgentUserId = "", FirstName = "Synthetic", CrmStage = "New", UpdatedUtc = revision });
        db.Add(new SubscriptionPayment { CommerceOrderId = order.Id, AmountCents = order.TotalCents, ProviderPaymentId = "legacy-reference" });
        await db.SaveChangesAsync();
        var actor = new MobileResolvedActor(new MessagingActor(profile.ClientUserId, MessagingParticipantTypes.Client), profile.Id, "Fixture");
        var actors = new Mock<IMobileActorResolver>();
        actors.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, new[] { actor }, actor, false));
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(db); services.AddSingleton(actors.Object);
            services.AddSingleton(Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>());
            services.AddScoped<Infrastructure.Businesses.BusinessWorkspaceService>();
            services.AddScoped<AgentPortal.Services.MobileAgentCrmService>();
            // Exercise the exact registration used by Portal Program, including exclusion of referenced MVC parts.
            services.AddControllers().AddApplicationPart(typeof(MobileWorkOrdersController).Assembly)
                .AddApplicationPart(typeof(ParfaitApp.Services.ParfaitOrderService).Assembly).AddMobileCommerceAuthority();
            services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
            services.AddAuthorization(x => x.AddPolicy(MobileApiAuthorization.PolicyName, p => p.RequireAuthenticatedUser()));
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        var features = new ControllerFeature();
        host.Services.GetRequiredService<ApplicationPartManager>().PopulateFeature(features);
        Assert.Contains(features.Controllers, x => x.AsType() == typeof(MobileWorkOrdersController));
        Assert.DoesNotContain(features.Controllers, x => x.Assembly == typeof(ParfaitApp.Services.ParfaitOrderService).Assembly);
        var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/mobile/work/orders")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-Actor", "owner");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/mobile/work/orders")).StatusCode);
        var workLead = Assert.Single((await client.GetFromJsonAsync<MobileWorkLeadsController.WorkLead[]>("/api/v1/mobile/work/leads"))!);
        var stage = new MobileWorkLeadsController.StageInput(workLead.Kind, workLead.RecordId, workLead.BusinessId, workLead.ProfileId,
            "Won", workLead.Stage, "Synthetic HTTP update", workLead.UpdatedUtc, workLead.Revision);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/mobile/work/leads/stage", stage with { BusinessId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/mobile/work/leads/stage", stage)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/v1/mobile/work/leads/stage", stage with { Stage = "Closed" })).StatusCode);
        var updatedLead = Assert.Single((await client.GetFromJsonAsync<MobileWorkLeadsController.WorkLead[]>("/api/v1/mobile/work/leads"))!);
        Assert.Equal("Won", updatedLead.Stage); Assert.Equal("completed", updatedLead.Bucket);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/commerce/manage/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/store/checkout")).StatusCode);
        var path = $"/api/v1/mobile/work/orders/{order.Id}/fulfillment";
        var input = new MobileWorkOrdersController.FulfillmentInput(business.Id, profile.Id, "Shipped", "Carrier", "tracking", revision);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, input with { ProfileId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/v1/mobile/work/orders/{Guid.NewGuid()}/fulfillment", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, input with { TrackingNumber = null })).StatusCode);
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ShippingLabelStateJson, "{\"Status\":\"PurchasePending\"}"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Empty(await db.MarketingDestinationDeliveries.ToListAsync());
        await db.CommerceOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ShippingLabelStateJson, (string?)null));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, input)).StatusCode);
        db.ChangeTracker.Clear();
        var saved = await db.CommerceOrders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal("Shipped", saved.FulfillmentStatus); Assert.NotNull(saved.ShippedUtc);
        Assert.Equal("retained-provider-reference", saved.PaymentReference);
        Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
        Assert.Equal("legacy-reference", (await db.SubscriptionPayments.SingleAsync()).ProviderPaymentId);
        Console.WriteLine("MOBILE_HTTP_SQL_PROVIDER:" + db.Database.ProviderName + ":PASS");
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Fixture-Actor")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "http-fixture-owner") }, "Fixture")), "Fixture"))
                : AuthenticateResult.NoResult());
    }
}
