using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Legend.Commerce;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ParfaitAnalyticsAccessTests
{
    [Theory]
    [InlineData(true, "/internal/analytics", true)]
    [InlineData(false, "/internal/analytics", false)]
    [InlineData(true, "/internal/commerce/orders", false)]
    public async Task Existing_page_grant_controls_shared_analytics_reads_and_callbacks(bool active, string assignedPage, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "legend-parfait-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(InternalModulesController).Assembly).AddLegendCommerceMvc();
            await using var app = builder.Build();
            var registry = new ParfaitInternalPageRegistry(app.Services.GetRequiredService<IActionDescriptorCollectionProvider>());
            await using var db = ControllerTestHelpers.BuildDb();
            var oid = Guid.NewGuid().ToString();
            var business = new Domain.Entities.CommerceBusiness { Key = "parfait", IsActive = true, Status = "Active" };
            var profile = new Domain.Entities.ClientProfile { ClientUserId = oid, Email = "synthetic-staff@mylegnd.com" };
            db.AddRange(business, profile, new Domain.Entities.CommerceBusinessMember {
                CommerceBusinessId=business.Id, ClientProfileId=profile.Id, RoleKey="staff", Status=active?"Active":"Disabled",
                CanManageAnalytics=true, CanManageStorefront=true, CanManageOrders=true,
                ParfaitPageGrantsJson=JsonSerializer.Serialize(new[]{assignedPage})
            });
            await db.SaveChangesAsync();
            var access = new ParfaitTeamAccessService(db, new ParfaitBusinessScopeService(db), registry, null!, null!, new HttpContextAccessor(), null!, Mock.Of<IGraphMailService>(), new ConfigurationBuilder().Build());
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", oid), new Claim(ClaimTypes.Email, "synthetic-staff@mylegnd.com") }, "synthetic-cookie"));
            foreach (var path in new[] { "/internal/analytics/summary", "/internal/analytics/commerce", "/internal/analytics/marketing-setup",
                "/internal/analytics/calendar-callback", "/internal/analytics/external-ads/callback" })
            {
                Assert.Equal(allowed, (await access.AuthorizePageAsync(user, path)).Allowed);
                using var scope = app.Services.CreateScope();
                var context = new DefaultHttpContext { User = user, RequestServices = scope.ServiceProvider };
                context.Request.Path = path;
                var reachedAction = false;
                await new ParfaitInternalAccessMiddleware(_ => { reachedAction = true; return Task.CompletedTask; }).InvokeAsync(context, access);
                Assert.Equal(allowed, reachedAction);
                if (!allowed) Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
