using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Middleware;
using AgentPortal.Mobile;
using AgentPortal.Security;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Domain.Accounts;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Shared.Analytics;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

[Collection("Profile website Founder environment")]
public sealed class ReceptionistPrivacyHttpTests
{
    [Fact]
    public async Task AuthenticatedFounderMetadataUsesTheSameZeroWritePipelineWithoutRenewal()
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", "22222222-2222-2222-2222-222222222222");
            using var f = await HttpFixture.CreateAsync();
            using var response = await f.SendAsync("/metadata/founder", cookie: true, humanCookie: true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.False(response.Headers.Contains("Set-Cookie"));
            f.AssertNoEffects();
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }
    [Fact]
    public async Task ActualRestrictedCookieRendersOnlyMetadataWithoutRenewalWritesOrProfileMaterialization()
    {
        using var f = await HttpFixture.CreateAsync();
        using var response = await f.SendAsync("/founder/engineering/receptionist/status", cookie: true);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join("\n", f.Counts.Failures)); Assert.Contains("Paused", body);
        Assert.Contains("Verified synthetic intake outcomes", body);
        Assert.Contains("dashboard-page-shell", body);
        Assert.Contains("/design/legend-web-foundation.css", body);
        Assert.Contains("/design/legend-app-shell.css", body);
        Assert.Contains("/_content/Shared/css/dashboard-home-shared.css", body);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canary", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        f.AssertNoEffects();
    }

    [Theory]
    [InlineData("/private/customer", true, false, false)]
    [InlineData("/api/v1/mobile/private", false, true, false)]
    [InlineData("/api/v1/mobile/private", true, true, true)]
    [InlineData("/private/export", true, false, false)]
    [InlineData("/private/hub", true, false, false)]
    [InlineData("/Home/ErrorStatus/404", true, false, false)]
    public async Task RestrictedCookiesBearerAndMixedCredentialsCannotReachHumanData(string path, bool cookie, bool bearer, bool humanCookie)
    {
        using var f = await HttpFixture.CreateAsync();
        using var response = await f.SendAsync(path, cookie, bearer, humanCookie);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("canary", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        f.AssertNoEffects();
        if (bearer) Assert.True(f.Counts.BearerAuthentications > 0);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "true")]
    [InlineData(true, "false")]
    [InlineData(false, "false")]
    public async Task RegisteredSubjectWithoutRestrictedRoleCannotUseAnotherHostsHumanSession(bool cookie, string enabled)
    {
        using var f = await HttpFixture.CreateAsync(enabled);
        using var response = await f.SendAsync(cookie ? "/private/customer" : "/api/v1/mobile/private",
            cookie: cookie, bearer: !cookie, ordinaryRole: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie")); f.AssertNoEffects();
    }

    [Theory]
    [InlineData("false", HttpStatusCode.Forbidden)]
    [InlineData("malformed", HttpStatusCode.ServiceUnavailable)]
    [InlineData(null, HttpStatusCode.Forbidden)]
    public async Task DisabledMissingAndMalformedRegistrationsNeverFallBackToHumanAccess(string? enabled, HttpStatusCode status)
    {
        using var f = await HttpFixture.CreateAsync(enabled);
        using var response = await f.SendAsync("/founder/engineering/receptionist/status", cookie: true);
        Assert.Equal(status, response.StatusCode); f.AssertNoEffects();
        using var privateResponse = await f.SendAsync("/private/customer", cookie: true);
        Assert.Equal(HttpStatusCode.Forbidden, privateResponse.StatusCode); f.AssertNoEffects();
    }

    [Fact]
    public async Task MetadataRejectsBearerBeforeAuthenticationHandlerOrBackchannelIsInvoked()
    {
        using var f = await HttpFixture.CreateAsync();
        f.Counts.ThrowOnBearer = true;
        using var response = await f.SendAsync("/founder/engineering/receptionist/status", cookie: true, bearer: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, f.Counts.BearerAuthentications); f.AssertNoEffects();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFailuresDoNotReexecuteHumanErrorPagesOrPersistDiagnostics(bool cancellation)
    {
        using var f = await HttpFixture.CreateAsync();
        using var response = await f.SendAsync(cancellation ? "/metadata/cancellation" : "/metadata/failure", cookie: true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("canary", await response.Content.ReadAsStringAsync());
        f.AssertNoEffects();
    }

    [Fact]
    public async Task ImpersonationCookieIsRejectedWithoutDecryptingOrClearingIt()
    {
        using var f = await HttpFixture.CreateAsync();
        using var response = await f.SendAsync("/founder/engineering/receptionist/status", cookie: true, impersonation: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie")); f.AssertNoEffects();
    }

    [Fact]
    public async Task DisabledAccountLifecycleCannotReachTheMetadataProjection()
    {
        using var f = await HttpFixture.CreateAsync(paused: true);
        using var response = await f.SendAsync("/founder/engineering/receptionist/status", cookie: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); f.AssertNoEffects();
    }

    [Fact]
    public async Task AuthorizedHumanRetainsExistingProfileInitializationAndHumanResponse()
    {
        using var f = await HttpFixture.CreateAsync();
        f.Guard.Armed = false;
        using var response = await f.SendAsync("/private/customer", cookie: true, humanCookie: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("human-response-canary", await response.Content.ReadAsStringAsync());
        Assert.True(f.Counts.TrackingCalls > 0); Assert.Equal(1, f.Counts.HumanHandlers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanOwnerAndBusinessRenderRealRazorAndKeepEveryReceptionistActionScoped(bool business)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        using var setup = await session.GetAsync(home);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var html = await setup.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Receptionist settings");
        Assert.Contains("Receptionist tasks", html);
        Assert.Contains("Optional appointments", html);
        Assert.Contains(Subpage(home, "/connections") + "#voice-connection", html);
        Assert.Equal(home, HtmlForm.Read(html, home).Action, ignoreCase: true);
        _ = HtmlForm.Read(html, Subpage(home, "/admission"));
        Assert.True(setup.Headers.CacheControl?.NoStore);

        using var management = await session.GetAsync(Subpage(home, "/connections"));
        Assert.Equal(HttpStatusCode.OK, management.StatusCode);
        var connections = await management.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(connections, "Receptionist connections");
        Assert.Contains("Back to receptionist home", connections);
        _ = HtmlForm.Read(connections, Subpage(home, "/connections/credential"));
        _ = HtmlForm.Read(connections, Subpage(home, "/connections/plan"));
        Assert.Contains("type=\"password\"", connections);
        Assert.True(management.Headers.CacheControl?.NoStore);
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Fact]
    public async Task HumanCanonicalHubDoesNotRequireAnEngineeringScopeIdentifier()
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        using var response = await f.NewHumanSession().GetAsync("/WebsiteAnalytics/receptionist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Receptionist settings");
        _ = HtmlForm.Read(html, "/WebsiteAnalytics/receptionist");
        Assert.Contains("/WebsiteAnalytics/receptionist/connections#voice-connection", html);
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HumanInvalidAndStaleSetupPostsRetainEditsWithoutChangingStoredSettings(bool business, bool stale)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        using var get = await session.GetAsync(home);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), home);
        var originalName = form.Value("BusinessName");
        var submittedRevision = Guid.NewGuid().ToString();
        form.Set("BusinessName", "Unsaved synthetic edit");
        // Staleness is checked by ReceptionistSetupForms.Apply before any transaction.
        // Malformed numeric input fails MVC model binding before Save is entered.
        if (stale) form.Set("ExpectedRevision", submittedRevision);
        else form.Set("MaximumCallSeconds", "not-a-duration");
        using var post = await session.PostAsync(form);
        Assert.Equal(stale ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, post.StatusCode);
        var html = await post.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Receptionist settings");
        Assert.Contains("role=\"alert\"", html);
        var retained = HtmlForm.Read(html, home);
        Assert.Equal("Unsaved synthetic edit", retained.Value("BusinessName"));
        if (stale)
        {
            Assert.Equal(submittedRevision, retained.Value("ExpectedRevision"));
            Assert.Contains("These settings changed while you were editing", html);
        }
        // Navigation/refresh and a newly authenticated session see saved data, not the rejected edit.
        foreach (var reader in new[] { session, f.NewHumanSession() })
        {
            using var reload = await reader.GetAsync(home);
            Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
            Assert.Equal(originalName, HtmlForm.Read(await reload.Content.ReadAsStringAsync(), home).Value("BusinessName"));
        }
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "input.")]
    [InlineData(true, "input.")]
    public async Task HumanInvalidCredentialPostDoesNotEchoPlaintextOrPrefixedSecret(bool business, string prefix)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        var action = Subpage(home, "/connections/credential");
        using var get = await session.GetAsync(Subpage(home, "/connections"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), action);
        var token = form.Value("__RequestVerificationToken");
        form.Values.Clear(); form.Set("__RequestVerificationToken", token);
        form.Set(prefix + "ExpectedConnectionRevision", "not-a-revision");
        form.Set(prefix + "ProviderProjectKey", "proj_synthetic_http");
        form.Set(prefix + "Purpose", "voice-api"); form.Set(prefix + "Secret", HumanSecret);
        using var post = await session.PostAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var html = await post.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Receptionist connections");
        Assert.Contains("For security, enter the credential again", html);
        Assert.Contains("proj_synthetic_http", html);
        Assert.Equal("", HtmlForm.Read(html, action).Value("Secret"));
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HumanMalformedAndExpiredReviewsRenderActionableFailureWithoutApply(bool business, bool malformed)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        using var get = await session.GetAsync(Subpage(home, "/connections"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), Subpage(home, "/connections/plan"));
        if (malformed) form.Set("sha256", "not-a-hash");
        using var post = await session.PostAsync(form);
        Assert.Equal(malformed ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, post.StatusCode);
        var html = await post.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Receptionist connections");
        Assert.Contains(malformed ? "Choose a valid saved operation" : "This review is expired, unavailable", html);
        Assert.Contains("#operation-reviews", html);
        Assert.DoesNotContain("action=\"" + Subpage(home, "/connections/apply"), html);
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanUnacceptedContentPreviewRendersRealComparisonWithoutApply(bool business)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var home = f.Home(business); var session = f.NewHumanSession();
        using var response = await session.GetAsync(Subpage(home, "/content"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Review receptionist content");
        Assert.Contains("No previously accepted provider configuration exists", html);
        Assert.Contains("Complete initial operator acceptance first", html);
        Assert.Contains("The reference content below is for comparison and does not establish current acceptance.", html);
        Assert.Contains("there is no accepted operating configuration selected", html);
        Assert.Contains("Applying still requires an accepted baseline, matching protected settings and content review, and keeps calls paused.", html);
        Assert.Contains("<summary>Reference content</summary>", html);
        Assert.DoesNotContain("accepted baseline version", html);
        Assert.DoesNotContain("This comparison uses historical acceptance", html);
        Assert.DoesNotContain("Previously applied content", html);
        Assert.Contains("href=\"" + home + "\"", html);
        Assert.DoesNotContain("action=\"" + Subpage(home, "/content/apply"), html);
        _ = HtmlForm.Read(html, Subpage(home, "/content/restore"));
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanInvalidContentRecoveryPreservesSelectionAndRevisionInRealHtml(bool business)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        var action = Subpage(home, "/content/restore");
        using var get = await session.GetAsync(Subpage(home, "/content"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), action);
        var originalRevision = form.Value("ExpectedRevision");
        form.Set("SourceVersionId", "not-a-version");
        using var post = await session.PostAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var html = await post.Content.ReadAsStringAsync();
        AssertCanonicalHumanHtml(html, "Review receptionist content");
        Assert.Contains("Your submitted choices remain below", html);
        Assert.Contains("not-a-version", html);
        var retained = HtmlForm.Read(html, action);
        Assert.Equal(originalRevision, retained.Value("ExpectedRevision"));
        Assert.Equal("not-a-version", retained.Value("SourceVersionId"));
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanFormPostsRequireTheActualAntiforgeryTokenAndMatchingCookie(bool business)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(business);
        using var get = await session.GetAsync(home);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), home);
        form.Set("MaximumCallSeconds", "not-a-duration");
        using var missing = await session.PostAsync(form, includeAntiforgery: false);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.DoesNotContain("Receptionist settings", await missing.Content.ReadAsStringAsync());
        var otherSession = f.NewHumanSession();
        using var otherGet = await otherSession.GetAsync(home);
        Assert.Equal(HttpStatusCode.OK, otherGet.StatusCode);
        using var mismatched = await otherSession.PostAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        Assert.DoesNotContain("Receptionist settings", await mismatched.Content.ReadAsStringAsync());
        using var matching = await session.PostAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, matching.StatusCode);
        AssertCanonicalHumanHtml(await matching.Content.ReadAsStringAsync(), "Receptionist settings");
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData("business_inactive")]
    [InlineData("member_inactive")]
    [InlineData("not_owner")]
    [InlineData("client_paused")]
    public async Task HumanBusinessRoutesHonorActualMembershipCapabilityAndLifecycle(string denial)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true, businessDenial: denial);
        var session = f.NewHumanSession();
        foreach (var suffix in new[] { "", "/connections", "/content" })
        {
            using var response = await session.GetAsync(Subpage(f.Home(true), suffix));
            AssertHumanCookieDenied(response);
            Assert.DoesNotContain("Receptionist settings", await response.Content.ReadAsStringAsync());
        }
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Fact]
    public async Task HumanValidAntiforgeryDoesNotAuthorizeForeignBusinessOrAggregateOwnerScope()
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        var session = f.NewHumanSession(); var home = f.Home(true);
        using var get = await session.GetAsync(home);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var form = HtmlForm.Read(await get.Content.ReadAsStringAsync(), home);
        form.Action = $"/business/{f.ForeignBusinessId}/receptionist";
        using var post = await session.PostAsync(form);
        AssertHumanCookieDenied(post);
        Assert.DoesNotContain("foreign-business-canary", await post.Content.ReadAsStringAsync());
        using var aggregate = await session.GetAsync(f.Home(false) + "&team=true");
        AssertHumanCookieDenied(aggregate);
        await f.AssertHumanNoProtectedEffectsAsync();
    }

    [Theory]
    [InlineData("/WebsiteAnalytics/receptionist")]
    [InlineData("/WebsiteAnalytics/receptionist/connections")]
    public async Task RestrictedIdentityStillCannotEnterRealHumanControllers(string path)
    {
        using var f = await HttpFixture.CreateAsync(humanJourney: true);
        // Restore the restricted guard before exercising the real registered controllers.
        f.Guard.Armed = true;
        using var response = await f.SendAsync(path, cookie: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("canary", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        f.AssertNoEffects();
    }

    private static void AssertHumanCookieDenied(HttpResponseMessage response)
    {
        // The unchanged cookie handler translates the controller's Forbid into this
        // browser redirect. Do not replace its events just to assert a raw 403 here.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
    }


    private const string HumanSecret = "synthetic-http-credential-canary-never-live";

    // This small parser reads only the concrete Razor form markup under test. It is
    // not a browser, a JavaScript runtime, or a claim of keyboard/visual acceptance.
    private sealed class HtmlForm(string action, List<KeyValuePair<string, string>> values)
    {
        internal string Action { get; set; } = action;
        internal List<KeyValuePair<string, string>> Values { get; } = values;
        internal string Value(string name) => Assert.Single(Values.Where(x => x.Key == name)).Value;
        internal void Set(string name, string value)
        { Values.RemoveAll(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)); Values.Add(new(name, value)); }
        internal static HtmlForm Read(string html, string expectedAction)
        {
            var forms = Regex.Matches(html, "<form\\b(?<attrs>[^>]*)>(?<body>[\\s\\S]*?)</form>", RegexOptions.IgnoreCase);
            var matches = forms.Cast<Match>().Where(x => Attribute(x.Groups["attrs"].Value, "action")
                .Equals(expectedAction, StringComparison.OrdinalIgnoreCase)).ToArray();
            var match = Assert.Single(matches);
            Assert.Equal("post", Attribute(match.Groups["attrs"].Value, "method"), ignoreCase: true);
            var fields = new List<KeyValuePair<string, string>>();
            var body = match.Groups["body"].Value;
            foreach (Match input in Regex.Matches(body, "<input\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase))
            {
                var attrs = input.Groups["attrs"].Value;
                var name = Attribute(attrs, "name"); var type = Attribute(attrs, "type").ToLowerInvariant();
                if (name.Length == 0 || Has(attrs, "disabled") || type is "submit" or "button" or "file") continue;
                if ((type is "checkbox" or "radio") && !Has(attrs, "checked")) continue;
                fields.Add(new(name, Attribute(attrs, "value")));
            }
            foreach (Match area in Regex.Matches(body, "<textarea\\b(?<attrs>[^>]*)>(?<value>[\\s\\S]*?)</textarea>", RegexOptions.IgnoreCase))
            {
                var name = Attribute(area.Groups["attrs"].Value, "name");
                if (name.Length > 0) fields.Add(new(name, WebUtility.HtmlDecode(area.Groups["value"].Value)));
            }
            foreach (Match select in Regex.Matches(body, "<select\\b(?<attrs>[^>]*)>(?<options>[\\s\\S]*?)</select>", RegexOptions.IgnoreCase))
            {
                var name = Attribute(select.Groups["attrs"].Value, "name");
                var options = Regex.Matches(select.Groups["options"].Value, "<option\\b(?<attrs>[^>]*)>(?<value>[\\s\\S]*?)</option>", RegexOptions.IgnoreCase).Cast<Match>().ToArray();
                var chosen = options.FirstOrDefault(x => Has(x.Groups["attrs"].Value, "selected")) ?? options.FirstOrDefault();
                if (name.Length > 0 && chosen is not null) fields.Add(new(name, Attribute(chosen.Groups["attrs"].Value, "value")));
            }
            var result = new HtmlForm(Attribute(match.Groups["attrs"].Value, "action"), fields);
            Assert.False(string.IsNullOrWhiteSpace(result.Value("__RequestVerificationToken")));
            return result;
        }
        private static string Attribute(string attributes, string name) => WebUtility.HtmlDecode(
            Regex.Match(attributes, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
        private static bool Has(string attributes, string name) =>
            Regex.IsMatch(attributes, "(?:^|\\s)" + Regex.Escape(name) + "(?:\\s|=|$)", RegexOptions.IgnoreCase);
    }

    private static string Subpage(string home, string suffix)
    {
        var parts = home.Split('?', 2);
        return parts[0] + suffix + (parts.Length == 2 ? "?" + parts[1] : "");
    }

    private static void AssertCanonicalHumanHtml(string html, string heading)
    {
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(heading, html);
        Assert.Contains("legend-app", html);
        Assert.Contains("dashboard-page-shell", html);
        Assert.Contains("/design/legend-web-foundation.css", html);
        Assert.Contains("/design/legend-app-shell.css", html);
        Assert.Contains("/_content/Shared/css/dashboard-home-shared.css", html);
        Assert.DoesNotContain(HumanSecret, html, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-business-canary", html, StringComparison.Ordinal);
    }


    public sealed class Counters
    {
        public int HumanHandlers, ErrorHandlers, DiagnosticWrites, TrackingCalls, BearerAuthentications;
        public bool ThrowOnBearer;
        public readonly List<string> Failures = [];
    }
    private sealed class DiagnosticSink(Counters counts) : IRuntimeDiagnosticSink
    {
        public Task RecordAsync(RuntimeDiagnosticEvent value, CancellationToken ct = default)
        { counts.DiagnosticWrites++; return Task.CompletedTask; }
    }
    private sealed class Logs : ILoggerProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string category) => new Writer(Messages);
        public void Dispose() { }
        private sealed class Writer(System.Collections.Concurrent.ConcurrentQueue<string> values) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (IsEnabled(level)) values.Enqueue(formatter(state, exception) + exception?.Message); }
        }
    }
    [Authorize]
    public sealed class BoundaryController(Counters counts) : Controller
    {
        [HttpGet("/private/{kind}")]
        public IActionResult Human() { counts.HumanHandlers++; return Content("human-response-canary"); }
        [HttpGet("/api/v1/mobile/private")]
        [Authorize(AuthenticationSchemes = MobileApiAuthorization.BearerScheme)]
        public IActionResult Mobile() { counts.HumanHandlers++; return Content("mobile-contact-canary"); }
        [HttpGet("/Home/ErrorStatus/{code}")]
        [HttpPost("/Home/ErrorStatus/{code}")] // Production ErrorStatus accepts status-page re-execution of POSTs too.
        [AllowAnonymous]
        public IActionResult Error() { counts.ErrorHandlers++; return Content("error-profile-canary"); }
        [HttpGet("/metadata/failure")]
        [ReceptionistMetadataRead(restrictedOperator: true)]
        public IActionResult Failure() => throw new InvalidOperationException("exception-contact-canary");
        [HttpGet("/metadata/founder")]
        [ReceptionistMetadataRead]
        public IActionResult FounderMetadata() => Json(new { state = "saved_metadata_only" });
        [HttpGet("/metadata/cancellation")]
        [ReceptionistMetadataRead(restrictedOperator: true)]
        public IActionResult Cancellation() => throw new OperationCanceledException("cancel-contact-canary");
    }
    private sealed class OnlyControllers(bool humanJourney = false) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo type) => type.AsType() == typeof(BoundaryController) ||
            type.AsType() == typeof(ReceptionistOperationsController) ||
            (humanJourney && (type.AsType() == typeof(WebsiteAnalyticsController) ||
                type.AsType() == typeof(AgentPortal.Controllers.BusinessWorkspaceController)));
    }
    private sealed class DataGuard : SaveChangesInterceptor, IMaterializationInterceptor
    {
        internal bool Armed;
        internal int Writes, IdentifyingMaterializations;
        public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
        { CheckWrite(); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { CheckWrite(); return ValueTask.FromResult(result); }
        private void CheckWrite() { if (Armed) { Writes++; throw new InvalidOperationException("Metadata must be zero-write."); } }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Armed && entity is AgentProfile or ClientProfile or WorkstationLeadProfile or WebsiteLead or MarketingConnection)
            { IdentifyingMaterializations++; throw new InvalidOperationException("Full identifying objects must not enter metadata."); }
            return entity;
        }
    }
    // Human middleware legitimately refreshes its synthetic AgentProfile. Every
    // receptionist/business write remains forbidden in this read/rejected-form fixture.
    private sealed class HumanDataGuard : SaveChangesInterceptor
    {
        internal bool Armed;
        internal int ProtectedWriteAttempts, ProfileWrites;
        public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
        { Check(data.Context); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { Check(data.Context); return ValueTask.FromResult(result); }
        private void Check(DbContext? context)
        {
            if (!Armed) return;
            var changed = context?.ChangeTracker.Entries().Where(x =>
                x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            if (changed is { Length: > 0 } && changed.All(x => x.State == EntityState.Modified &&
                x.Entity is AgentProfile profile && profile.AgentUserId == HttpFixture.Human))
            { ProfileWrites++; return; }
            ProtectedWriteAttempts++;
            throw new InvalidOperationException("Rejected receptionist forms must not write protected state.");
        }
    }
    private sealed class HttpFixture : IDisposable
    {
        private const string Subject = "11111111-1111-1111-1111-111111111111";
        internal const string Human = "22222222-2222-2222-2222-222222222222";
        internal const string HumanUpn = "human-canary@example.invalid";
        internal readonly Guid HumanTrackingId = Guid.NewGuid();
        internal readonly Guid BusinessId = Guid.NewGuid();
        internal readonly Guid ForeignBusinessId = Guid.NewGuid();
        private const string Tenant = "33333333-3333-3333-3333-333333333333";
        private const string Audience = "44444444-4444-4444-4444-444444444444";
        private const string Issuer = "https://synthetic.invalid/" + Tenant + "/v2.0";
        private readonly SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes("synthetic-test-signing-material-never-a-live-credential-0123456789"));
        internal readonly Counters Counts = new();
        internal readonly DataGuard Guard = new();
        private readonly HumanDataGuard humanGuard = new();
        private readonly Logs logs = new();
        private readonly List<Action> verifyUnusedServices = [];
        private string? previousFounderOid;
        private bool humanJourney;
        private DbContextOptions<MasterAppDbContext> databaseOptions = null!;
        private IHost host = null!;
        private HttpClient client = null!;
        internal static async Task<HttpFixture> CreateAsync(string? enabled = "true", bool paused = false,
            bool humanJourney = false, string? businessDenial = null)
        {
            var f = new HttpFixture { humanJourney = humanJourney };
            try
            {
                if (humanJourney)
                {
                    f.previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
                    Environment.SetEnvironmentVariable("FOUNDER_OID", Human);
                }
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:TenantId"] = Tenant,
                    ["Founder:Upn"] = HumanUpn,
                    ["FOUNDER_OID"] = humanJourney ? Human : null,
                    ["Receptionist:RestrictedOperators:synthetic:SubjectId"] = enabled is null ? "unrelated-subject" : Subject,
                    ["Receptionist:RestrictedOperators:synthetic:TenantId"] = Tenant,
                    ["Receptionist:RestrictedOperators:synthetic:Enabled"] = enabled,
                    ["Receptionist:RestrictedOperators:synthetic:OwnerType"] = "founder"
                }).Build();
                var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase("receptionist-http-" + Guid.NewGuid())
                    .AddInterceptors(f.Guard, f.humanGuard).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning,
                        CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
                f.databaseOptions = options;
                await using (var db = new MasterAppDbContext(options))
                {
                    var profile = new AgentProfile { AgentUserId = Subject, AgentUpn = "profile-canary@example.invalid", NormalizedEmail = "profile-canary@example.invalid" };
                    db.AgentProfiles.Add(profile);
                    db.AgentProfiles.Add(new AgentProfile { AgentUserId = Human, AgentUpn = "human-canary@example.invalid", NormalizedEmail = "human-canary@example.invalid" });
                    if (paused) db.AccountLifecycleRecords.Add(new() { UserId = Subject, ParticipantType = Domain.Messaging.MessagingParticipantTypes.Agent,
                        ProfileId = profile.Id, State = AccountLifecycleStates.Paused });
                    var configuration = new ReceptionistBusinessConfiguration { BusinessName = "Synthetic", ConversationInstructions = "private-policy-canary",
                        BusinessPolicy = "private-policy-canary", TimeZoneId = "UTC", Currency = "USD", MaximumCallSeconds = 120,
                        MaximumConcurrentCalls = 1, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 2 };
                    var json = ReceptionistConfigurationCodec.Serialize(configuration);
                    var connection = new MarketingConnection { OwnerType = "founder", OwnerKey = MarketingOwnerScope.Founder.Key, Provider = "openai" };
                    var version = new ReceptionistConfigurationVersion { MarketingConnectionId = connection.Id, Version = 23, ConfigurationJson = json,
                        ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(json), CreatedByActorId = "synthetic" };
                    connection.ReceptionistActiveConfigurationId = version.Id;
                    db.Add(connection); db.Add(version);
                    if (humanJourney)
                    {
                        db.AgentTrackingProfiles.Add(new AgentTrackingProfile { Id = f.HumanTrackingId,
                            AgentUserId = Human, AgentUpn = HumanUpn, Slug = "human-synthetic", DisplayName = "Synthetic owner" });
                        var clientProfile = new ClientProfile { ClientUserId = Human, Email = HumanUpn,
                            FirstName = "Synthetic", LastName = "owner" };
                        db.ClientProfiles.Add(clientProfile);
                        db.CommerceBusinesses.Add(new CommerceBusiness { Id = f.BusinessId, Key = "synthetic-http",
                            DisplayName = "Synthetic business", IsActive = businessDenial != "business_inactive", Status = "Active" });
                        db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = f.BusinessId,
                            ClientProfileId = clientProfile.Id, RoleKey = businessDenial == "not_owner" ? "viewer" : "owner",
                            Status = businessDenial == "member_inactive" ? "Inactive" : "Active" });
                        db.AccountLifecycleRecords.Add(new() { UserId = Human, ParticipantType = Domain.Messaging.MessagingParticipantTypes.Client,
                            ProfileId = clientProfile.Id, State = businessDenial == "client_paused" ? AccountLifecycleStates.Paused : AccountLifecycleStates.Active });
                        // A foreign business is real and active, but this actor has no member link.
                        db.CommerceBusinesses.Add(new CommerceBusiness { Id = f.ForeignBusinessId,
                            Key = "foreign-http", DisplayName = "foreign-business-canary" });
                        var businessConnection = new MarketingConnection { OwnerType = "business",
                            OwnerKey = MarketingOwnerScope.Business(f.BusinessId).Key, CommerceBusinessId = f.BusinessId, Provider = "openai" };
                        var businessJson = ReceptionistConfigurationCodec.Serialize(configuration with { BusinessName = "Synthetic business" });
                        var businessVersion = new ReceptionistConfigurationVersion { MarketingConnectionId = businessConnection.Id,
                            Version = 23, ConfigurationJson = businessJson, ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(businessJson),
                            CreatedByActorId = "synthetic" };
                        businessConnection.ReceptionistActiveConfigurationId = businessVersion.Id;
                        db.Add(businessConnection); db.Add(businessVersion);
                        foreach (var pair in new[] { (Connection: connection, Version: version), (Connection: businessConnection, Version: businessVersion) })
                        {
                            // Deliberately expired synthetic review label; the payload is never valid for apply.
                            const string payload = "{}";
                            db.Set<ReceptionistOperationPackage>().Add(new() { MarketingConnectionId = pair.Connection.Id,
                                ConfigurationVersionId = pair.Version.Id, Operation = "set_owner_admission",
                                PayloadJson = payload, PayloadSha256 = ReceptionistConfigurationCodec.Hash(payload),
                                ReviewState = "accepted", ReviewedPayloadSha256 = ReceptionistConfigurationCodec.Hash(payload),
                                CreatedByActorId = "synthetic", ReviewedByActorId = Human,
                                ReviewedUtc = DateTime.UtcNow.AddHours(-2), ExpiresUtc = DateTime.UtcNow.AddHours(-1) });
                        }
                    }
                    await db.SaveChangesAsync();
                }
                var tracking = new Mock<IAgentTrackingService>(MockBehavior.Strict);
                tracking.Setup(x => x.EnsureProfileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .Callback(() => f.Counts.TrackingCalls++).ReturnsAsync(new AgentTrackingProfile { AgentUserId = Human, Slug = "human-synthetic" });
                if (humanJourney)
                {
                    var trackingProfile = new AgentTrackingProfile { Id = f.HumanTrackingId, AgentUserId = Human,
                        AgentUpn = HumanUpn, Slug = "human-synthetic", DisplayName = "Synthetic owner" };
                    tracking.Setup(x => x.GetByUserIdAsync(Human, It.IsAny<CancellationToken>())).ReturnsAsync(trackingProfile);
                    tracking.Setup(x => x.GetPersonalUrlsAsync(It.Is<AgentTrackingProfile>(p => p.Id == f.HumanTrackingId), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new AgentUrlInfo("https://synthetic.invalid/a/human-synthetic"));
                }
                var lifecycle = new Mock<IAccountLifecycleService>(MockBehavior.Strict);
                lifecycle.Setup(x => x.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AccountLifecycleSnapshot(AccountLifecycleStates.Active, true, false, null, null, null));
                f.host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureLogging(log => log.ClearProviders().AddProvider(f.logs))
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IConfiguration>(config); services.AddSingleton(f.Counts); services.AddSingleton(options);
                        services.AddSingleton<IRuntimeDiagnosticSink>(new DiagnosticSink(f.Counts));
                        services.AddScoped(_ => new MasterAppDbContext(options));
                        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                        services.AddSingleton(tracking.Object); services.AddSingleton(lifecycle.Object);
                        services.AddScoped<FounderImpersonationService>(); services.AddScoped<AssistantContextService>(); services.AddScoped<AgentRegistryService>();
                        services.AddScoped<AgentProfileAccessResolver>(); services.AddScoped<ReceptionistBookingReadinessReader>();
                        services.AddSingleton(TimeProvider.System); services.AddScoped<ReceptionistAdmissionService>();
                        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
                            {
                                o.Cookie.Name = "synthetic-auth"; o.SlidingExpiration = true; o.ExpireTimeSpan = TimeSpan.FromHours(1);
                                ReceptionistMetadataAccess.ConfigureCookieObservation(o, config);
                            })
                            .AddJwtBearer(MobileApiAuthorization.BearerScheme, o =>
                            {
                                MobileBearerOptions.Configure(o, new(Tenant, Issuer, Audience, "api://" + Audience + "/access"));
                                o.Authority = null; o.Configuration = new() { Issuer = Issuer };
                                o.TokenValidationParameters.IssuerSigningKey = f.key;
                                o.Events.OnMessageReceived = _ =>
                                {
                                    f.Counts.BearerAuthentications++;
                                    if (f.Counts.ThrowOnBearer) throw new InvalidOperationException("Forbidden synthetic backchannel.");
                                    return Task.CompletedTask;
                                };
                            });
                        services.AddAuthorization();
                        if (humanJourney) f.AddHumanServices(services);
                        services.AddControllersWithViews(o =>
                        {
                            o.Filters.Add<AgentTrackingProvisioningFilter>(); o.Filters.Add<AgentAccountLifecycleAuthorizeFilter>();
                        }).AddApplicationPart(typeof(ReceptionistOperationsController).Assembly)
                        .AddApplicationPart(Assembly.Load("AgentPortal.Views")).AddApplicationPart(typeof(BoundaryController).Assembly)
                        .ConfigureApplicationPartManager(manager =>
                        {
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                            manager.FeatureProviders.Add(new OnlyControllers(humanJourney));
                        });
                    }).Configure(app =>
                    {
                        app.UseLegendFailureDiagnostics("SyntheticReceptionistHttp");
                        app.UseStatusCodePagesWithReExecute("/Home/ErrorStatus/{0}");
                        app.UseRouting(); app.UseAuthentication();
                        app.UseMiddleware<RestrictedAiAccessBoundary>(); app.UseMiddleware<ReceptionistMetadataAccessMiddleware>();
                        app.Use(async (context, next) =>
                        {
                            try { await next(context); }
                            catch (Exception error) { f.Counts.Failures.Add(error.ToString()); throw; }
                        });
                        app.UseMiddleware<FounderImpersonationMiddleware>(); app.UseMiddleware<AssistantResolutionMiddleware>();
                        app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
                    })).StartAsync();
                f.client = f.host.GetTestClient();
                f.Guard.Armed = !humanJourney;
                f.humanGuard.Armed = humanJourney;
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal Task<HttpResponseMessage> SendAsync(string path, bool cookie = false, bool bearer = false, bool humanCookie = false, bool impersonation = false, bool ordinaryRole = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookie)
            {
                var identity = new ClaimsIdentity([new Claim("oid", humanCookie ? Human : Subject), new Claim("tid", Tenant),
                    new Claim("name", "identity-canary"), new Claim("preferred_username", "identity-canary@example.invalid"),
                    new Claim(ClaimTypes.Role, humanCookie || ordinaryRole ? "Agent" : RestrictedAiIdentity.Role)], "Cookies");
                var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
                var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-40), ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20) };
                var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, "Cookies"));
                request.Headers.Add("Cookie", "synthetic-auth=" + ticket + (impersonation ? "; legend.impersonation=invalid-canary" : ""));
            }
            if (bearer)
            {
                var token = new JwtSecurityToken(Issuer, Audience, [new("oid", Subject), new("tid", Tenant),
                    new("roles", ordinaryRole ? "Agent" : RestrictedAiIdentity.Role), new("scp", "access")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20),
                    new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            }
            return client.SendAsync(request);
        }
        private void AddHumanServices(IServiceCollection services)
        {
            services.AddHttpContextAccessor();
            services.AddScoped<EffectiveAgentContext>();
            services.AddScoped<MarketingConnectionStore>();
            services.AddScoped<MarketingCredentialProtector>();
            services.AddScoped<MetaCapiCredentialProtector>();
            services.AddScoped<ReceptionistOperatorService>();
            services.AddScoped<BusinessWorkspaceService>();
            services.AddScoped<Shared.PortalShell.IPortalShellIdentityResolver, AgentPortalShellIdentityResolver>();
            services.AddScoped<WebsiteAnalyticsAiDataBuilder>();
            Unused<IAnalyticsQueryService>(); Unused<IMetaAdsService>(); Unused<IMetaAdsOAuthService>();
            Unused<IMetaSignalAnalyticsService>(); Unused<ILandingRouteDiscoveryService>();
            Unused<IVisitorConcentrationService>(); Unused<IKpiDetailBreakdownService>();
            Unused<IVisitorTrustScoringService>(); Unused<IAnalyticsIncidentQueryService>();
            Unused<IUnifiedMarketingPerformanceService>(); Unused<IPromotionOrchestrationService>();

            // No AddMarketingConnections, HttpClient, hosted worker, provider authority or network transport.
            void Unused<T>() where T : class
            {
                var mock = new Mock<T>(MockBehavior.Strict);
                services.AddSingleton(mock.Object);
                verifyUnusedServices.Add(() => Assert.Empty(mock.Invocations));
            }
        }

        internal HumanSession NewHumanSession()
        {
            Assert.True(humanJourney, "Human requests require the explicit synthetic mode.");
            return new HumanSession(this);
        }

        internal string Home(bool business) => business ? $"/business/{BusinessId}/receptionist"
            : $"/WebsiteAnalytics/receptionist?agentProfileId={HumanTrackingId}";

        internal async Task AssertHumanNoProtectedEffectsAsync()
        {
            Assert.Equal(0, humanGuard.ProtectedWriteAttempts);
            Assert.True(humanGuard.ProfileWrites > 0); // The real human middleware was not bypassed.
            Assert.Empty(Counts.Failures);
            Assert.Equal(0, Counts.DiagnosticWrites);
            foreach (var verify in verifyUnusedServices) verify();
            Assert.DoesNotContain(logs.Messages, x => x.Contains(HumanSecret, StringComparison.Ordinal));
            await using var db = new MasterAppDbContext(databaseOptions);
            Assert.Equal(2, await db.MarketingConnections.CountAsync());
            Assert.Equal(2, await db.Set<ReceptionistConfigurationVersion>().CountAsync());
            Assert.Empty(await db.Set<ReceptionistCredentialVersion>().ToListAsync());
            Assert.Equal(2, await db.Set<ReceptionistOperationPackage>().CountAsync());
            Assert.Empty(await db.Set<ReceptionistCall>().ToListAsync());
            Assert.All(await db.MarketingConnections.ToListAsync(), x => Assert.False(x.ReceptionistAdmissionEnabled));
        }

        // TestServer does not emulate a browser cookie jar. Retain the actual auth and
        // antiforgery cookies issued by this server; never manufacture an antiforgery token.
        internal sealed class HumanSession
        {
            private readonly HttpFixture fixture;
            private readonly CookieContainer cookies = new();
            internal HumanSession(HttpFixture fixture)
            {
                this.fixture = fixture;
                var identity = new ClaimsIdentity([new Claim("oid", Human), new Claim("tid", Tenant),
                    new Claim(ClaimTypes.NameIdentifier, Human), new Claim(ClaimTypes.Name, "Synthetic owner"),
                    new Claim("name", "Synthetic owner"), new Claim("preferred_username", HumanUpn),
                    new Claim(ClaimTypes.Role, "Agent")], "Cookies");
                var options = fixture.host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
                var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity),
                    new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20) }, "Cookies"));
                cookies.SetCookies(fixture.client.BaseAddress!, "synthetic-auth=" + ticket + "; path=/");
            }
            internal Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
            internal Task<HttpResponseMessage> PostAsync(HtmlForm form, bool includeAntiforgery = true) =>
                SendAsync(HttpMethod.Post, form.Action, includeAntiforgery ? form.Values :
                    form.Values.Where(x => x.Key != "__RequestVerificationToken"));
            private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path,
                IEnumerable<KeyValuePair<string, string>>? fields = null)
            {
                var uri = new Uri(fixture.client.BaseAddress!, path);
                Assert.Equal(fixture.client.BaseAddress!.Authority, uri.Authority);
                Assert.Equal(fixture.client.BaseAddress.Scheme, uri.Scheme);
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Add("Cookie", cookies.GetCookieHeader(uri));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
                var response = await fixture.client.SendAsync(request);
                if (response.Headers.TryGetValues("Set-Cookie", out var values))
                    foreach (var value in values) cookies.SetCookies(uri, value);
                return response;
            }
        }

        internal void AssertNoEffects()
        {
            Assert.Equal(0, Guard.Writes); Assert.Equal(0, Guard.IdentifyingMaterializations);
            Assert.Equal(0, Counts.TrackingCalls); Assert.Equal(0, Counts.HumanHandlers);
            Assert.Equal(0, Counts.ErrorHandlers); Assert.Equal(0, Counts.DiagnosticWrites);
            Assert.DoesNotContain(logs.Messages, x => x.Contains("canary", StringComparison.OrdinalIgnoreCase));
        }
        public void Dispose()
        {
            client?.Dispose(); host?.Dispose();
            if (humanJourney) Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }
}
