using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Mobile;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistPrivacyEntryTests
{
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private static IConfigurationRoot Registry(bool registered = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receptionist:RestrictedOperators:synthetic:SubjectId"] = registered ? Subject : "different-subject",
            ["Receptionist:RestrictedOperators:synthetic:Enabled"] = "false"
        }).Build();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProductionSignInWrapperRejectsRestrictedCallbacksBeforeResolvingHumanServices(bool registered, bool restrictedRole)
    {
        var configuration = Registry(registered);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("oid", Subject),
            new(ClaimTypes.Role, restrictedRole ? RestrictedAiIdentity.Role : "Agent")], "validated"));
        var context = new DefaultHttpContext(); // No human service registrations are available.
        var token = new TokenValidatedContext(context, new("oidc", null, typeof(OpenIdConnectHandler)),
            new(), principal, new());
        var continuations = 0;
        Task Continue() { continuations++; return Task.CompletedTask; }
        await RestrictedAiIdentity.ContinueHumanSignInAsync(token.Principal, configuration,
            () => token.Fail("Restricted operator access is unavailable in this application."), Continue);
        var restricted = registered || restrictedRole;
        Assert.Equal(restricted ? 0 : 1, continuations);
        Assert.Equal(restricted, token.Result?.Failure is not null);
        var cookie = new CookieValidatePrincipalContext(context, new("Cookies", null, typeof(CookieAuthenticationHandler)),
            new(), new AuthenticationTicket(principal, "Cookies")) { ShouldRenew = true };
        continuations = 0;
        await RestrictedAiIdentity.ContinueHumanSignInAsync(cookie.Principal, configuration,
            () => { cookie.RejectPrincipal(); cookie.ShouldRenew = false; }, Continue);
        Assert.Equal(restricted ? 0 : 1, continuations);
        Assert.Equal(!restricted, cookie.ShouldRenew);
        Assert.Equal(restricted, cookie.Principal is null);
    }

    [Fact]
    public async Task ValidMobileBookingTicketForRestrictedSubjectIsDeniedBeforeAnyDataOrProviderAccess()
    {
        var protection = new EphemeralDataProtectionProvider();
        var ticket = protection.CreateProtector("AgentPortal.MobileClientBooking.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new { AgentId = Subject, ProfileId = Guid.NewGuid(), TimeZone = "UTC" }), TimeSpan.FromMinutes(20));
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(Registry()).BuildServiceProvider();
        var controller = new MobileClientBookingController(new Mock<IMobileActorResolver>(MockBehavior.Strict).Object,
            null!, protection, new Mock<IAgentTimeZoneResolver>(MockBehavior.Strict).Object, null!)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        Assert.IsType<ForbidResult>(await controller.Page(ticket, default));
        controller.Request.Headers["X-Legend-Booking-Ticket"] = ticket;
        Assert.IsType<ForbidResult>(await controller.Create(new(), default));
        Assert.False(controller.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task ValidBusinessTicketForRestrictedSubjectIsDeniedBeforeCalendarConnectionMaintenance()
    {
        using var tickets = new BusinessBookingTicketProtector(new EphemeralDataProtectionProvider());
        var ticket = tickets.Protect(new(Guid.NewGuid(), Guid.NewGuid(), Subject, "Synthetic operator", "UTC", DateTime.UtcNow.AddMinutes(20)));
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(Registry()).BuildServiceProvider();
        var connections = new Mock<IMicrosoftCalendarConnectionAuthority>(MockBehavior.Strict);
        var controller = new InternalBusinessBookingController(null!, tickets, null!, connections.Object)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        controller.Request.Headers["X-Legend-Business-Booking-Ticket"] = ticket;
        Assert.IsType<ForbidResult>(await controller.Status(default));
        Assert.IsType<ForbidResult>(await controller.Create(new(), default));
        connections.VerifyNoOtherCalls(); Assert.False(controller.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task PreviouslyIssuedHumanCreationTicketCannotBypassLaterRestrictedClassification()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new() { AgentUserId = Subject, AgentUpn = "synthetic-agent@example.invalid", IsActive = true });
        await db.SaveChangesAsync();
        var configuration = Registry(false);
        configuration["GraphProvisioning:TenantId"] = "synthetic-tenant";
        configuration["GraphProvisioning:ClientId"] = "synthetic-client";
        configuration["GraphProvisioning:ClientSecret"] = "synthetic-unused-constructor-value";
        var controller = ControllerTestHelpers.BuildClientsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(Subject, "synthetic-agent@example.invalid"), configuration: configuration,
            mobileActorResolver: new MobileActorResolver(db, NullLogger<MobileActorResolver>.Instance));
        var routes = new Mock<IUrlHelper>();
        routes.Setup(x => x.RouteUrl(It.IsAny<UrlRouteContext>())).Returns((UrlRouteContext route) =>
            "/mobile/agent/clients/create?ticket=" + Uri.EscapeDataString((string)new RouteValueDictionary(route.Values)["ticket"]!));
        controller.Url = routes.Object;
        var launch = Assert.IsType<ClientsController.MobileClientCreationPortalLaunch>(
            Assert.IsType<OkObjectResult>(await controller.MobileClientCreationPortalLaunchRequest(default)).Value);
        var ticket = Uri.UnescapeDataString(launch.LaunchPath.Split("?ticket=")[1]);
        configuration["Receptionist:RestrictedOperators:synthetic:SubjectId"] = Subject;
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        // Any query, including an Any projection, now throws rather than exposing a record.
        await db.DisposeAsync();
        Assert.IsType<ForbidResult>(await controller.MobileClientCreationPortal(ticket, default));
        Assert.IsType<ForbidResult>(await controller.MobileClientCreationPortalSubmit(ticket,
            new CreateClientViewModel { RecordType = "Client" }, "/", CancellationToken.None));
    }
}
