using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ParfaitApp.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

{
    var configuredOwnerEmails = builder.Configuration
        .GetSection("Founder:OwnerEmails")
        .Get<string[]>()?
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .ToList()
        ?? [];

    var configuredPrimaryOwnerEmail = builder.Configuration["Founder:Email"]?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredPrimaryOwnerEmail) &&
        !configuredOwnerEmails.Any(value => value.Equals(configuredPrimaryOwnerEmail, StringComparison.OrdinalIgnoreCase)))
    {
        configuredOwnerEmails.Insert(0, configuredPrimaryOwnerEmail);
    }

    var resolvedOwnerEmails =
        Environment.GetEnvironmentVariable("OWNER_EMAILS")
        ?? Environment.GetEnvironmentVariable("OwnerEmails");

    if (string.IsNullOrWhiteSpace(resolvedOwnerEmails) && configuredOwnerEmails.Count > 0)
        resolvedOwnerEmails = string.Join(';', configuredOwnerEmails);

    if (!string.IsNullOrWhiteSpace(resolvedOwnerEmails))
        Environment.SetEnvironmentVariable("OWNER_EMAILS", resolvedOwnerEmails);

    var resolvedOwnerEmail =
        Environment.GetEnvironmentVariable("OWNER_EMAIL")
        ?? Environment.GetEnvironmentVariable("OwnerEmail")
        ?? configuredOwnerEmails.FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(resolvedOwnerEmail))
        Environment.SetEnvironmentVariable("OWNER_EMAIL", resolvedOwnerEmail);

    var resolvedFounderOid =
        Environment.GetEnvironmentVariable("FOUNDER_OID")
        ?? Environment.GetEnvironmentVariable("FounderOid")
        ?? builder.Configuration["Founder:Oid"]?.Trim();

    if (!string.IsNullOrWhiteSpace(resolvedFounderOid))
        Environment.SetEnvironmentVariable("FOUNDER_OID", resolvedFounderOid);
}

static string? ResolveMasterDb(IConfiguration config)
{
    var cs = Environment.GetEnvironmentVariable("SQLCONNSTR_MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = Environment.GetEnvironmentVariable("MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = config.GetConnectionString("MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    return null;
}

var configuredDb = ResolveMasterDb(builder.Configuration);

builder.Services.AddDbContext<MasterAppDbContext>(options =>
{
    if (string.IsNullOrWhiteSpace(configuredDb))
        throw new InvalidOperationException("Missing MasterAppDb connection string for Parfait analytics.");

    if (configuredDb.TrimStart().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(configuredDb);
    else
        options.UseSqlServer(configuredDb);
});

builder.Services.AddSingleton<ParfaitProductService>();
builder.Services.AddSingleton<ParfaitOrderService>();
builder.Services.AddSingleton<IParfaitInternalPageRegistry, ParfaitInternalPageRegistry>();
builder.Services.AddScoped<IParfaitTeamAccessService, ParfaitTeamAccessService>();
builder.Services.AddHttpClient<SquarePaymentService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ParfaitAnalyticsService>();
builder.Services.AddScoped<ParfaitMetaSignalBridgeService>();
builder.Services.AddScoped<ParfaitAnalyticsDashboardService>();
builder.Services.AddScoped<ParfaitInternalWorkspaceService>();
builder.Services.AddScoped<IGraphMailService, GraphMailService>();
builder.Services.AddSingleton<ParfaitMetaCapiCredentialProtector>();
builder.Services.AddScoped<IParfaitBusinessProfileService, ParfaitBusinessProfileService>();
builder.Services.AddScoped<IParfaitMetaAdsOAuthService, ParfaitMetaAdsOAuthService>();

static bool TryResolveCanonicalHost(HttpRequest request, out HostString host)
{
    host = request.Host;
    var incomingHost = request.Host.Host?.Trim();
    if (string.IsNullOrWhiteSpace(incomingHost))
        return false;

    if (!incomingHost.Equals("www.shopparfait.com", StringComparison.OrdinalIgnoreCase))
        return false;

    host = request.Host.Port.HasValue
        ? new HostString("shopparfait.com", request.Host.Port.Value)
        : new HostString("shopparfait.com");

    return true;
}

static string BuildExternalCallbackUrl(HttpRequest request, PathString path)
{
    var host = TryResolveCanonicalHost(request, out var canonicalHost)
        ? canonicalHost
        : request.Host;

    return UriHelper.BuildAbsolute(
        request.Scheme,
        host,
        request.PathBase,
        path);
}

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "ParfaitApp.InternalAuth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/internal/login";
        options.LogoutPath = "/internal/logout";
        options.AccessDeniedPath = "/internal/denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnValidatePrincipal = async context =>
        {
            var teamAccess = context.HttpContext.RequestServices.GetRequiredService<IParfaitTeamAccessService>();
            if (await teamAccess.ValidatePrincipalAsync(context.Principal!, context.HttpContext.RequestAborted))
                return;

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
    })
    .AddOpenIdConnect(options =>
    {
        var tenantId = builder.Configuration["AzureAd:TenantId"];
        var clientId = builder.Configuration["AzureAd:ClientId"];
        var clientSecret = builder.Configuration["AzureAd:ClientSecret"];

        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = true;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.SignedOutRedirectUri = "/";
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.IsEssential = true;
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.HttpOnly = true;
        options.NonceCookie.IsEssential = true;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri =
                BuildExternalCallbackUrl(context.Request, context.Options.CallbackPath);
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.PostLogoutRedirectUri =
                BuildExternalCallbackUrl(context.Request, context.Options.SignedOutCallbackPath);
            return Task.CompletedTask;
        };

        options.Events.OnRemoteFailure = context =>
        {
            var error = Uri.EscapeDataString(context.Failure?.Message ?? "Authentication failed.");
            context.Response.Redirect($"/internal/denied?message={error}");
            context.HandleResponse();
            return Task.CompletedTask;
        };

        options.Events.OnTokenValidated = async context =>
        {
            var teamAccess = context.HttpContext.RequestServices.GetRequiredService<IParfaitTeamAccessService>();
            var result = await teamAccess.AuthorizeSignInAsync(context.Principal!, context.HttpContext.RequestAborted);
            if (!result.Allowed)
                context.Fail(result.Message);
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        TryResolveCanonicalHost(context.Request, out var canonicalHost))
    {
        var redirectUrl = UriHelper.BuildAbsolute(
            context.Request.Scheme,
            canonicalHost,
            context.Request.PathBase,
            context.Request.Path,
            context.Request.QueryString);

        context.Response.Redirect(redirectUrl, permanent: true);
        return;
    }

    await next();
});
app.UseStaticFiles();
app.UseRouting();

app.UseSession();

app.UseAuthentication();
app.UseMiddleware<ParfaitInternalAccessMiddleware>();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] = "upgrade-insecure-requests; block-all-mixed-content";
    await next();
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    app.Urls.Clear();
    app.Urls.Add($"http://*:{port}");
}

app.Run();
