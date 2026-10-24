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
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Middleware;
using AgentPortal.Mobile;
using AgentPortal.Security;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Accounts;
using Domain.Entities;
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
    private sealed class OnlyControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo type) => type.AsType() == typeof(BoundaryController) ||
            type.AsType() == typeof(ReceptionistOperationsController);
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
    private sealed class HttpFixture : IDisposable
    {
        private const string Subject = "11111111-1111-1111-1111-111111111111";
        private const string Human = "22222222-2222-2222-2222-222222222222";
        private const string Tenant = "33333333-3333-3333-3333-333333333333";
        private const string Audience = "44444444-4444-4444-4444-444444444444";
        private const string Issuer = "https://synthetic.invalid/" + Tenant + "/v2.0";
        private readonly SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes("synthetic-test-signing-material-never-a-live-credential-0123456789"));
        internal readonly Counters Counts = new();
        internal readonly DataGuard Guard = new();
        private readonly Logs logs = new();
        private IHost host = null!;
        private HttpClient client = null!;
        internal static async Task<HttpFixture> CreateAsync(string? enabled = "true", bool paused = false)
        {
            var f = new HttpFixture();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:TenantId"] = Tenant,
                ["Receptionist:RestrictedOperators:synthetic:SubjectId"] = enabled is null ? "unrelated-subject" : Subject,
                ["Receptionist:RestrictedOperators:synthetic:TenantId"] = Tenant,
                ["Receptionist:RestrictedOperators:synthetic:Enabled"] = enabled,
                ["Receptionist:RestrictedOperators:synthetic:OwnerType"] = "founder"
            }).Build();
            var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase("receptionist-http-" + Guid.NewGuid())
                .AddInterceptors(f.Guard).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning,
                    CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
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
                db.Add(connection); db.Add(version); await db.SaveChangesAsync();
            }
            var tracking = new Mock<IAgentTrackingService>(MockBehavior.Strict);
            tracking.Setup(x => x.EnsureProfileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => f.Counts.TrackingCalls++).ReturnsAsync(new AgentTrackingProfile { AgentUserId = Human, Slug = "human-synthetic" });
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
                    services.AddControllersWithViews(o =>
                    {
                        o.Filters.Add<AgentTrackingProvisioningFilter>(); o.Filters.Add<AgentAccountLifecycleAuthorizeFilter>();
                    }).AddApplicationPart(typeof(ReceptionistOperationsController).Assembly)
                    .AddApplicationPart(Assembly.Load("AgentPortal.Views")).AddApplicationPart(typeof(BoundaryController).Assembly)
                    .ConfigureApplicationPartManager(manager =>
                    {
                        foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                        manager.FeatureProviders.Add(new OnlyControllers());
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
            f.client = f.host.GetTestClient(); f.Guard.Armed = true; return f;
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
        internal void AssertNoEffects()
        {
            Assert.Equal(0, Guard.Writes); Assert.Equal(0, Guard.IdentifyingMaterializations);
            Assert.Equal(0, Counts.TrackingCalls); Assert.Equal(0, Counts.HumanHandlers);
            Assert.Equal(0, Counts.ErrorHandlers); Assert.Equal(0, Counts.DiagnosticWrites);
            Assert.DoesNotContain(logs.Messages, x => x.Contains("canary", StringComparison.OrdinalIgnoreCase));
        }
        public void Dispose() { client.Dispose(); host.Dispose(); }
    }
}
