using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AgentPortal.Controllers.API;
using AgentPortal.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace AgentPortal.Tests;

// Uses the existing serialized Founder environment collection: authority is
// exercised with the real configured OID, never a test bypass or role substitute.
[Collection("LegendConnectFounderEnvironment")]
public sealed class WebDiagnosticPrivacyTests
{
    private const string FounderId = "dd690c96-5c32-4d10-a4cf-3b85cb630d78";
    private const string OtherId = "8ca749be-2b0d-4709-bab6-f9c90b147967";
    private const string PrivateMarker = "private-customer-secret@example.test";
    private const string RequestId = "0HNFIXTURE:00000001";

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void HomeError_ExposesOnlyGenericGuidanceAndRequestId(bool portal, bool json, bool founder)
    {
        using var environment = new FounderEnvironment();
        var context = Context(json, founder);
        context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature
        {
            Error = new InvalidOperationException(PrivateMarker, new Exception("inner-" + PrivateMarker)),
            Path = "/Customers/" + PrivateMarker
        });
        var controller = Home(portal, context);
        var result = portal
            ? ((AgentPortal.Controllers.HomeController)controller).Error()
            : ((ClientApp.Controllers.HomeController)controller).Error();

        AssertPublicResult(result, context, json, 500, "request_failed");
        Assert.Equal(portal && founder ? "/founder/diagnostics" : null, controller.ViewData["FounderDiagnosticsUrl"]);
    }

    [Theory]
    [InlineData(true, true, 404)]
    [InlineData(true, false, 403)]
    [InlineData(false, true, 503)]
    [InlineData(false, false, 404)]
    public void StatusErrors_PreserveHttpStatusWithoutReturningReexecutionDetails(bool portal, bool json, int status)
    {
        using var environment = new FounderEnvironment();
        var context = Context(json, founder: false);
        context.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature
        {
            OriginalPath = "/Customers/" + PrivateMarker,
            OriginalQueryString = "?token=" + PrivateMarker,
            OriginalPathBase = "/private"
        });
        var controller = Home(portal, context);
        var result = portal
            ? ((AgentPortal.Controllers.HomeController)controller).ErrorStatus(status)
            : ((ClientApp.Controllers.HomeController)controller).ErrorStatus(status);

        AssertPublicResult(result, context, json, status, "request_failed");
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void AccessDenied_NeverEchoesReturnTargetOrIdentity(bool portal, bool json, bool founder)
    {
        using var environment = new FounderEnvironment();
        var context = Context(json, founder);
        // AccessDenied does not use the login dependencies. Keeping them absent
        // verifies error rendering cannot start a sign-in, lookup or network call.
        Controller controller = portal ? new AgentPortal.Controllers.AccessController()
            : new ClientApp.Controllers.AccountController(null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var target = "/Messaging/private-thread?returnUrl=%2FCustomers%3Femail%3D" + PrivateMarker;
        var result = portal ? ((AgentPortal.Controllers.AccessController)controller).Denied(target)
            : ((ClientApp.Controllers.AccountController)controller).AccessDenied(target);

        AssertPublicResult(result, context, json, 403, "access_denied");
        Assert.Equal(portal && founder ? "/founder/diagnostics" : null, controller.ViewData["FounderDiagnosticsUrl"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BookingsDiagnostics_UsesCanonicalFounderAuthorizationBeforeGraphAccess(bool authenticated, bool founder)
    {
        using var environment = new FounderEnvironment();
        var type = typeof(BookingsDiagnosticsController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        var filter = Assert.IsType<FounderOnlyAttribute>(type.GetCustomAttribute<FounderOnlyAttribute>());
        var cache = Assert.IsType<ResponseCacheAttribute>(type.GetCustomAttribute<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        var context = Context(json: true, founder, authenticated);
        var authorization = new AuthorizationFilterContext(
            new ActionContext(context, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());

        filter.OnAuthorization(authorization);

        if (authenticated && founder) Assert.Null(authorization.Result);
        else Assert.IsType<ForbidResult>(authorization.Result);
    }

    private static Controller Home(bool portal, HttpContext context)
    {
        Controller controller = portal ? new AgentPortal.Controllers.HomeController() : new ClientApp.Controllers.HomeController();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static DefaultHttpContext Context(bool json, bool founder, bool authenticated = true)
    {
        var context = new DefaultHttpContext { TraceIdentifier = RequestId };
        context.Request.Path = "/Home/Error";
        context.Request.QueryString = new QueryString("?secret=" + PrivateMarker);
        context.Request.Headers.Accept = json ? "application/json" : "text/html";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", founder ? FounderId : OtherId),
            new Claim(ClaimTypes.NameIdentifier, FounderId), // not canonical authority
            new Claim(ClaimTypes.Role, "Founder"), // not canonical authority
            new Claim(ClaimTypes.Name, PrivateMarker), new Claim(ClaimTypes.Email, PrivateMarker)
        }, authenticated ? "Fixture" : null));
        foreach (var name in new[] { "X-Legend-Failure-Kind", "X-Legend-Failing-Point", "X-Legend-Redirect-Depth" })
            context.Response.Headers[name] = PrivateMarker;
        return context;
    }

    private static void AssertPublicResult(IActionResult result, HttpContext context, bool json, int status, string error)
    {
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(RequestId, context.Response.Headers["X-Legend-Request-Id"].ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        foreach (var name in new[] { "X-Legend-Failure-Kind", "X-Legend-Failing-Point", "X-Legend-Redirect-Depth" })
            Assert.False(context.Response.Headers.ContainsKey(name));
        if (json)
        {
            var response = Assert.IsType<ObjectResult>(result);
            Assert.Equal(status, response.StatusCode);
            var body = JsonSerializer.Serialize(response.Value);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(new[] { "error", "message", "requestId" }, document.RootElement.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal));
            Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
            Assert.Equal(RequestId, document.RootElement.GetProperty("requestId").GetString());
            Assert.DoesNotContain(PrivateMarker, body, StringComparison.Ordinal);
            Assert.DoesNotContain("Diagnostics", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var response = Assert.IsType<ViewResult>(result);
            Assert.NotNull(response.Model);
            // Portal and ClientApp intentionally have their own ErrorViewModel.
            var model = response.Model!;
            Assert.Null(model.GetType().GetProperty("Diagnostics")!.GetValue(model));
            Assert.Equal(RequestId, model.GetType().GetProperty("RequestId")!.GetValue(model));
            Assert.DoesNotContain(PrivateMarker, JsonSerializer.Serialize(model), StringComparison.Ordinal);
        }
    }

    private sealed class FounderEnvironment : IDisposable
    {
        private readonly string? _founder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        private readonly string? _environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        public FounderEnvironment()
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", _founder);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _environment);
        }
    }
}
