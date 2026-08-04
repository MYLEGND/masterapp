using Infrastructure.DailyScripture;
using Infrastructure.Data;
using Infrastructure.Billing;
using Infrastructure.FinancialIntelligence;
using Infrastructure.Messaging;
using Infrastructure.Identity;
using Infrastructure.Households;
using ClientApp.Infrastructure;
using ClientApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Shared.Diagnostics;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// MVC + DI
// ------------------------------------------------------------
builder.Services.AddControllersWithViews(options =>
{
    // Force auth globally
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.Filters.Add(new AuthorizeFilter(policy));
    options.Filters.AddService<ClientAccountLifecycleAuthorizeFilter>();
    options.Filters.AddService<ClientSubscriptionAuthorizeFilter>();
})
    .AddApplicationPart(typeof(MessagingHub).Assembly);

// Browser anti-forgery: use the platform header convention (matches AgentPortal
// and existing ClientApp AJAX callers) so cookie-authenticated same-origin
// mutations can present the token via the RequestVerificationToken header.
// Protection is applied explicitly per state-changing action ([ValidateAntiForgeryToken]);
// no global auto-validate filter is added here.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddHttpContextAccessor();
builder.Services.AddDailyScripture();
builder.Services.AddMasterAppBilling(builder.Configuration);
builder.Services.AddMasterAppFinancialIntelligence(builder.Configuration);
builder.Services.AddMasterAppMessaging(builder.Configuration);
builder.Services.AddScoped<EffectiveClientContextService>();
builder.Services.AddScoped<ClientProfileImageLegacyBackfillService>();
builder.Services.AddHostedService<ClientProfileImageLegacyBackfillHostedService>();
builder.Services.AddScoped<IMessagingActorContextResolver, ClientAppMessagingActorContextResolver>();
builder.Services.AddSingleton<ClientAppReturnUrlNormalizer>();
builder.Services.AddScoped<IClientEntraLifecycleService, ClientEntraLifecycleService>();
builder.Services.AddScoped<IAccountLifecycleService, AccountLifecycleService>();
builder.Services.AddScoped<IHouseholdMembershipService, HouseholdMembershipService>();
builder.Services.AddScoped<IClientSubscriptionIdentitySyncService, ClientSubscriptionIdentitySyncService>();
builder.Services.AddScoped<ClientIdentityContinuationService>();
builder.Services.AddScoped<ClientIdentityAccessService>();
builder.Services.AddScoped<ClientAppSignInEntryPoint>();
builder.Services.AddScoped<SubscriptionActivationService>();
builder.Services.AddScoped<ClientSubscriptionAuthorizeFilter>();
builder.Services.AddScoped<ClientAccountLifecycleAuthorizeFilter>();
builder.Services.AddScoped<IAuthorizationHandler, ClientSubscriptionActiveHandler>();
// Data Protection — platform authority. Application name is preserved
// ("MasterApp.ClientApp") so purpose isolation is unchanged; this now PERSISTS
// the key ring (Azure Blob + Key Vault in production, otherwise file-system)
// instead of the previous ephemeral in-memory ring, so cookies / continuation
// tokens survive restarts and work across scaled-out instances.
Shared.Security.PlatformConfigValidation.ValidateDataProtection(
    builder.Configuration, builder.Environment.IsProduction());
Infrastructure.Security.PlatformDataProtection.AddPlatformDataProtection(
    builder.Services,
    builder.Configuration,
    builder.Environment,
    "MasterApp.ClientApp");

// Reverse-proxy forwarded headers (was missing) for correct HTTPS/HSTS/redirect
// behavior and client IP behind Azure's TLS-terminating proxy.
Shared.Security.PlatformSecurityHeaders.AddPlatformForwardedHeaders(builder.Services);
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 64 * 1024;
});
builder.Services.AddRateLimiter(options =>
{
    // Shared fixed-window construction (one authority); ClientApp's login/public
    // policy names and limits are app profiles. IP partitioning preserved exactly.
    Infrastructure.Security.PlatformRateLimiting.AddFixedWindowPolicy(options, "clientapp-public", 20, TimeSpan.FromMinutes(1));
    Infrastructure.Security.PlatformRateLimiting.AddFixedWindowPolicy(options, "clientapp-login", 8, TimeSpan.FromMinutes(1));

    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
});
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.Secure = CookieSecurePolicy.Always;
});

// ------------------------------------------------------------
// DB CONNECTION RESOLUTION
// ------------------------------------------------------------
static bool IsSqlServerConn(string? cs)
{
    if (string.IsNullOrWhiteSpace(cs)) return false;

    // Azure SQL / SQL Server signatures
    return cs.Contains("Server=tcp:", StringComparison.OrdinalIgnoreCase)
        || cs.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase)
        || cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
        || cs.Contains("Authentication=", StringComparison.OrdinalIgnoreCase);
}

static bool IsSqliteConn(string? cs)
{
    if (string.IsNullOrWhiteSpace(cs)) return false;
    return cs.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase);
}

static string ExtractSqlitePath(string connString)
{
    var parts = connString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var p in parts)
    {
        if (p.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            return p.Substring("Data Source=".Length).Trim().Trim('"');
    }
    return "";
}

static string ResolveSqliteConnectionString(string? configuredConnString, IWebHostEnvironment env)
{
    // If explicitly set and sqlite, honor it when the file exists.
    if (!string.IsNullOrWhiteSpace(configuredConnString) && IsSqliteConn(configuredConnString))
    {
        var configured = configuredConnString.Trim();
        var configuredPath = ExtractSqlitePath(configured);
        if (string.IsNullOrWhiteSpace(configuredPath) || File.Exists(configuredPath))
            return configured;
    }

    // Local dev fallback
    if (env.IsDevelopment())
    {
        var siblingAgentDb = Path.Combine(env.ContentRootPath, "..", "AgentPortal", "App_Data", "masterapp.db");
        if (File.Exists(siblingAgentDb))
            return $"Data Source={Path.GetFullPath(siblingAgentDb)}";

        var workspaceDb = Path.Combine(env.ContentRootPath, "..", "App_Data", "masterapp.db");
        if (File.Exists(workspaceDb))
            return $"Data Source={Path.GetFullPath(workspaceDb)}";

        Directory.CreateDirectory("App_Data");
        return "Data Source=App_Data/masterapp.db";
    }

    // Production fallback (ONLY used if you didn't provide SQL Server)
    var home = Environment.GetEnvironmentVariable("HOME");
    if (string.IsNullOrWhiteSpace(home))
        home = "D:\\home";

    var dataDir = Path.Combine(home, "data");
    Directory.CreateDirectory(dataDir);

    var dbFile = Path.Combine(dataDir, "masterapp.db");
    return $"Data Source={dbFile}";
}

static void EnsureSqliteDirectoryExists(string sqliteConnString)
{
    if (!IsSqliteConn(sqliteConnString)) return;

    var path = ExtractSqlitePath(sqliteConnString);
    if (string.IsNullOrWhiteSpace(path)) return;

    var dir = Path.GetDirectoryName(path);
    if (string.IsNullOrWhiteSpace(dir)) return;

    Directory.CreateDirectory(dir);
}

// Pull the connection string Azure injects (or secrets/local)
var configuredDb = builder.Configuration.GetConnectionString("MasterAppDb");

// Development provider selection:
// - If Azure SQL connection string exists, use it by default for parity with live data.
// - Set USE_SQLITE_IN_DEV=true to force local SQLite.
// - Legacy toggle USE_SQLSERVER_IN_DEV=false also forces SQLite.
var forceSqliteInDev = string.Equals(
    Environment.GetEnvironmentVariable("USE_SQLITE_IN_DEV"),
    "true",
    StringComparison.OrdinalIgnoreCase);

var disableSqlServerInDev = string.Equals(
    Environment.GetEnvironmentVariable("USE_SQLSERVER_IN_DEV"),
    "false",
    StringComparison.OrdinalIgnoreCase);

if (builder.Environment.IsDevelopment() && (forceSqliteInDev || disableSqlServerInDev) && IsSqlServerConn(configuredDb))
    configuredDb = null;

// Decide provider
var useSqlServer = IsSqlServerConn(configuredDb) && !IsSqliteConn(configuredDb);

// NO SQLITE FALLBACK IN PRODUCTION
if (!builder.Environment.IsDevelopment() && !useSqlServer)
{
    throw new InvalidOperationException(
        "PRODUCTION MISCONFIG: MasterAppDb connection string not found or not Azure SQL. " +
        "Set App Service > Configuration > Connection strings: name=MasterAppDb type=SQLAzure."
    );
}

// Dev-only sqlite fallback
var sqliteConn = useSqlServer ? null : ResolveSqliteConnectionString(configuredDb, builder.Environment);
if (sqliteConn != null)
    EnsureSqliteDirectoryExists(sqliteConn);

// DB
builder.Services.AddDbContext<MasterAppDbContext>(options =>
{
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    if (useSqlServer)
    {
        options.UseSqlServer(configuredDb!);
    }
    else
    {
        options.UseSqlite(sqliteConn!);
    }
});

// ------------------------------------------------------------
// AUTH CONFIG
// ------------------------------------------------------------
var tenantId = builder.Configuration["AzureAd:TenantId"] ?? "3fd90b17-12b1-4572-8cab-b0ceee317a30";
var clientId = builder.Configuration["AzureAd:ClientId"] ?? "96aab50e-61c5-4cb0-a79a-032dc8c1cb6c";
var callbackPath = builder.Configuration["AzureAd:CallbackPath"] ?? "/signin-oidc";
var clientSecret = (
    builder.Configuration["AzureAd:ClientSecret"]
    ?? builder.Configuration["AzureAd__ClientSecret"]
    ?? Environment.GetEnvironmentVariable("AzureAd__ClientSecret")
    ?? Environment.GetEnvironmentVariable("AzureAd:ClientSecret")
)?.Trim();

if (string.IsNullOrWhiteSpace(clientSecret))
{
    var localSecretCommand = "dotnet user-secrets set \"AzureAd:ClientSecret\" \"<CLIENTAPP_SECRET_VALUE>\" --project ClientApp/ClientApp.csproj";
    throw new InvalidOperationException(
        builder.Environment.IsDevelopment()
            ? "ClientApp AzureAd:ClientSecret is missing for Azure AD app client id 96aab50e-61c5-4cb0-a79a-032dc8c1cb6c. " +
              $"Set the secret locally with: {localSecretCommand}"
            : "ClientApp AzureAd:ClientSecret is missing. Set AzureAd__ClientSecret in App Service configuration."
    );
}

static bool IsExpiredOidcGrant(string? error, string? description)
{
    if (string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
        return true;

    if (string.IsNullOrWhiteSpace(description))
        return false;

    return description.Contains("AADSTS70008", StringComparison.OrdinalIgnoreCase) ||
           description.Contains("expired due to inactivity", StringComparison.OrdinalIgnoreCase) ||
           description.Contains("authorization code", StringComparison.OrdinalIgnoreCase) && description.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
           description.Contains("refresh token", StringComparison.OrdinalIgnoreCase) && description.Contains("expired", StringComparison.OrdinalIgnoreCase);
}

static bool IsCorrelationFailure(string? description)
{
    if (string.IsNullOrWhiteSpace(description))
        return false;

    return description.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase);
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.None;

    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;

    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
})
.AddOpenIdConnect(options =>
{
    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    options.ClientId = clientId;
    if (!string.IsNullOrWhiteSpace(clientSecret))
        options.ClientSecret = clientSecret;
    options.CallbackPath = callbackPath;
    options.CorrelationCookie.SameSite = SameSiteMode.None;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.NonceCookie.SameSite = SameSiteMode.None;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = false;
    options.GetClaimsFromUserInfoEndpoint = true;

    options.TokenValidationParameters = new TokenValidationParameters
    {
        NameClaimType = "name",
        RoleClaimType = "roles"
    };

    // ✅ Ensure OID claim exists and is easy to read consistently
    options.Events = new OpenIdConnectEvents
    {
        OnTokenValidated = context =>
        {
            var identity = context.Principal?.Identity as ClaimsIdentity;
            if (identity == null) return Task.CompletedTask;

            // Prefer "oid", but some stacks provide objectidentifier URI
            var oid =
                identity.FindFirst("oid")?.Value
                ?? identity.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if (!string.IsNullOrWhiteSpace(oid))
            {
                // Normalize: always have a simple "oid" claim
                if (identity.FindFirst("oid") == null)
                    identity.AddClaim(new Claim("oid", oid.Trim()));
            }

            return CompleteClientSignInAsync(context);
        },

        // Return 401 for AJAX requests instead of redirecting to Azure AD
        OnRedirectToIdentityProvider = ctx =>
        {
            // The client sign-in form has already verified this address belongs to an
            // active subscription. Forward that verified value to Entra so the account
            // picker starts with the same email instead of making the client re-enter it.
            if (ctx.Properties?.Parameters.TryGetValue("login_hint", out var loginHint) == true &&
                loginHint is string verifiedEmail &&
                !string.IsNullOrWhiteSpace(verifiedEmail))
            {
                ctx.ProtocolMessage.LoginHint = verifiedEmail;
            }

            if (ctx.Properties?.Parameters.TryGetValue("prompt", out var prompt) == true &&
                prompt is string promptValue &&
                !string.IsNullOrWhiteSpace(promptValue))
            {
                ctx.ProtocolMessage.Prompt = promptValue;
            }

            if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.HandleResponse();
                return Task.CompletedTask;
            }

            OidcTransientCookieCleanup.Clear(ctx.HttpContext, callbackPath);
            return Task.CompletedTask;
        },

        OnRemoteFailure = async ctx =>
        {
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIdConnect");
            var description = ctx.Failure?.InnerException?.Message ?? ctx.Failure?.Message ?? string.Empty;
            var error = description.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                ? "invalid_grant"
                : string.Empty;

            logger.LogWarning(ctx.Failure,
                "OIDC remote failure. Error={Error} Description={Description}",
                error,
                description);

            if (!IsExpiredOidcGrant(error, description) && !IsCorrelationFailure(description))
                return;

            var returnUrl = ctx.HttpContext.RequestServices
                .GetRequiredService<ClientAppReturnUrlNormalizer>()
                .Normalize(ctx.Properties?.RedirectUri);
            var loginUrl = $"{ctx.Request.PathBase}/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

            OidcTransientCookieCleanup.Clear(ctx.HttpContext, callbackPath);
            ctx.HttpContext.RequestServices
                .GetRequiredService<ClientIdentityAccessService>()
                .ClearChallengeContinuationCookie(ctx.HttpContext.Response);
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.HandleResponse();
            ctx.Response.Redirect(loginUrl);
        }
    };
});

builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Events.OnRedirectToLogin = async ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var signInEntryPoint = ctx.HttpContext.RequestServices.GetRequiredService<ClientAppSignInEntryPoint>();
        ctx.Response.Redirect(signInEntryPoint.Resolve(ctx.HttpContext));
    };

    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        ClientAppAuthorizationPolicies.ClientSubscriptionActive,
        policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ClientSubscriptionActiveRequirement());
        });
});

async Task CompleteClientSignInAsync(TokenValidatedContext context)
{
    var identityAccess = context.HttpContext.RequestServices.GetRequiredService<ClientIdentityAccessService>();
    var returnUrlNormalizer = context.HttpContext.RequestServices.GetRequiredService<ClientAppReturnUrlNormalizer>();
    var completion = await identityAccess.CompleteClientSignInAsync(
        context.HttpContext,
        context.Principal!,
        context.Properties?.RedirectUri,
        context.HttpContext.RequestAborted);

    if (completion.Success)
    {
        context.Properties!.RedirectUri = returnUrlNormalizer.Normalize(completion.ReturnUrl);
        return;
    }

    OidcTransientCookieCleanup.Clear(context.HttpContext, callbackPath);
    identityAccess.ClearChallengeContinuationCookie(context.HttpContext.Response);
    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.HandleResponse();
    context.Response.Redirect(
        $"/Account/ActivationRequired?returnUrl={Uri.EscapeDataString(returnUrlNormalizer.Normalize(completion.ReturnUrl))}&message={Uri.EscapeDataString(completion.SanitizedMessage ?? "The client sign-in could not be completed.")}");
}

var app = builder.Build();

// ------------------------------------------------------------
// LOG BOOT CONFIG (SAFE)
// ------------------------------------------------------------
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BootConfig");
    var aadSecret = app.Configuration["AzureAd:ClientSecret"];

    log.LogWarning("ENV={Env} ContentRoot={ContentRoot}", app.Environment.EnvironmentName, app.Environment.ContentRootPath);

    if (useSqlServer)
        log.LogWarning("DB Provider = SQLSERVER (Azure SQL).");
    else
        log.LogWarning("DB Provider = SQLITE. Conn={Conn}", sqliteConn);

    log.LogWarning("AzureAd:TenantId={TenantId} ClientId={ClientId} CallbackPath={CallbackPath}", tenantId, clientId, callbackPath);
    log.LogWarning("AzureAd secret present? {Present} len={Len}", !string.IsNullOrWhiteSpace(aadSecret), aadSecret?.Length ?? 0);
}

// Forwarded headers must run before HTTPS redirection/HSTS so the scheme is
// correct behind the Azure proxy. Baseline security headers added platform-wide.
app.UseForwardedHeaders();
Shared.Security.PlatformSecurityHeaders.UsePlatformSecurityHeaders(app);

// ------------------------------------------------------------
// HARD PRODUCTION EXCEPTION HANDLER (CAN'T FAIL)
// ------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseLegendFailureDiagnostics("ClientApp");
app.UseStatusCodePagesWithReExecute("/Home/ErrorStatus", "?statusCode={0}");

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();

app.UseCookiePolicy();
app.UseAuthentication();
app.UseAuthorization();

// Cache prevention
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

// ------------------------------------------------------------
// STARTUP CHECKS (DON'T KILL SITE)
// ------------------------------------------------------------
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupChecks");

    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        if (!useSqlServer && sqliteConn != null)
            EnsureSqliteDirectoryExists(sqliteConn);

        // ✅ IMPORTANT: Do NOT auto-run migrations in production for ClientApp.
        // Migrations should be applied by you (CI/CD or manual) to Azure SQL.
        if (app.Environment.IsDevelopment())
        {
            db.Database.Migrate();
            logger.LogInformation("DEV: DB migration completed OK.");
        }
        else
        {
            logger.LogWarning("PROD: Skipping db.Database.Migrate() in ClientApp (by design).");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "DB migration check failed.");
    }
}

// ✅ MVC endpoints
app.MapControllers();
app.MapHub<MessagingHub>("/messaginghub");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"
);

app.Run();
