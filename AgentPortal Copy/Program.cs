using AgentPortal.Hubs;
using AgentPortal.Middleware;
using AgentPortal.Services;
using Azure.Identity;
using Infrastructure.Data;
using AgentPortal.Security;
using AgentPortal.Services.Analytics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using AgentPortal.Services.Tracking;
using Microsoft.EntityFrameworkCore;
using LegendApp.Services.Budget;
using LegendApp.Services.Budget.Interfaces;
using Microsoft.Identity.Web.UI;
using QuestPDF.Infrastructure;
using System.Threading.RateLimiting;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// QuestPDF license (Community; change if revenue threshold exceeded)
QuestPDF.Settings.License = LicenseType.Community;

// ------------------------------------------------------------
// CORS for tracking/lead ingest (allow list from config)
// ------------------------------------------------------------

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
// ------------------------------------------------------------
// MVC + DI
// ------------------------------------------------------------
builder.Services
    .AddControllersWithViews(options =>
    {
        // Force auth globally
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
        
        options.Filters.Add(new AuthorizeFilter(policy));
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        options.Filters.Add<AgentTrackingProvisioningFilter>();
    })
    .AddMicrosoftIdentityUI();

// ✅ REQUIRED: Identity UI endpoints are Razor Pages
builder.Services.AddRazorPages().AddMicrosoftIdentityUI();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FounderOnly", policy =>
    {
        policy.RequireAssertion(ctx => FounderGuard.IsFounder(ctx.User));
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientProvisioningService>();
builder.Services.AddScoped<AssistantContextService>();
builder.Services.AddScoped<AgentRegistryService>();
builder.Services.AddScoped<AgencyCommandService>();
builder.Services.AddScoped<FounderImpersonationService>();
builder.Services.AddScoped<ProductionService>();
builder.Services.AddScoped<EffectiveAgentContext>();
builder.Services.AddScoped<IAdvancedMarketsCalculationService, AdvancedMarketsCalculationService>();
builder.Services.AddSingleton<IAgentTimeZoneResolver, AgentTimeZoneResolver>();
builder.Services.AddSingleton<IBudgetCalculator, BudgetCalculator>();
builder.Services.AddSingleton<ILeadBridgeStateService, LeadBridgeStateService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<IAgentTrackingService, AgentTrackingService>();
builder.Services.AddScoped<AgentTrackingProvisioningFilter>();
builder.Services.AddScoped<AgentTrackingResolver>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSignalR();

// CORS for ingest endpoints (allow local testing + portal host)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("TrackingCors", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});
var dataProtectionKeys = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(dataProtectionKeys);
builder.Services.AddDataProtection()
    .SetApplicationName("AgentPortal")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeys));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("anon-public", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value ?? "";
        // Allow SignalR hubs/websockets without limiting
        if (path.StartsWith("/livesync", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/leadbridgehub", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("hub");
        }

        var key = context.User?.Identity?.IsAuthenticated == true
            ? (context.User.Identity?.Name ?? "auth-unknown")
            : (context.Connection.RemoteIpAddress?.ToString() ?? "anon");

        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});

// Forwarded headers (required on Azure behind reverse proxy)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.Secure = CookieSecurePolicy.Always;
});

// ------------------------------------------------------------
// DB CONNECTION RESOLUTION (your logic preserved)
// ------------------------------------------------------------
static bool IsSqlServerConn(string? cs)
{
    if (string.IsNullOrWhiteSpace(cs)) return false;

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

static void EnsureSqliteDirectoryExists(string sqliteConnString)
{
    if (!IsSqliteConn(sqliteConnString)) return;

    var path = ExtractSqlitePath(sqliteConnString);
    if (string.IsNullOrWhiteSpace(path)) return;

    var dir = Path.GetDirectoryName(path);
    if (string.IsNullOrWhiteSpace(dir)) return;

    Directory.CreateDirectory(dir);
}

static void EnsureSqliteBackup(string sqliteConnString, int keepLatest = 20)
{
    if (!IsSqliteConn(sqliteConnString)) return;

    var dbPath = ExtractSqlitePath(sqliteConnString);
    if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;

    var dbDir = Path.GetDirectoryName(dbPath);
    if (string.IsNullOrWhiteSpace(dbDir)) return;

    var backupDir = Path.Combine(dbDir, "backups");
    Directory.CreateDirectory(backupDir);

    // Avoid creating multiple backups in the same day.
    var dayStamp = DateTime.UtcNow.ToString("yyyyMMdd");
    var existingToday = Directory.EnumerateFiles(backupDir, $"agentportal_masterapp_{dayStamp}_*.db").Any();
    if (!existingToday)
    {
        var fileName = $"agentportal_masterapp_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
        var target = Path.Combine(backupDir, fileName);
        File.Copy(dbPath, target, overwrite: false);
    }

    // Keep only the latest N backups.
    var backups = Directory.EnumerateFiles(backupDir, "agentportal_masterapp_*.db")
        .Select(p => new FileInfo(p))
        .OrderByDescending(f => f.LastWriteTimeUtc)
        .ToList();

    foreach (var old in backups.Skip(Math.Max(keepLatest, 1)))
    {
        try { old.Delete(); } catch { /* no-op */ }
    }
}

static string? ResolveMasterDb(IConfiguration config)
{
    var cs = config.GetConnectionString("MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = Environment.GetEnvironmentVariable("SQLCONNSTR_MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    cs = Environment.GetEnvironmentVariable("MasterAppDb");
    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();

    return null;
}

static string ResolveDevSqlite(string contentRootPath)
{
    var appDataPath = Path.Combine(contentRootPath, "App_Data");

    // If launched from workspace root (e.g., dotnet run --project AgentPortal),
    // keep using the AgentPortal-local database instead of workspace-level App_Data.
    if (!Directory.Exists(appDataPath))
    {
        var projectAppDataPath = Path.Combine(contentRootPath, "AgentPortal", "App_Data");
        var projectFolder = Path.Combine(contentRootPath, "AgentPortal");
        if (Directory.Exists(projectFolder))
            appDataPath = projectAppDataPath;
    }

    Directory.CreateDirectory(appDataPath);
    var dbPath = Path.Combine(appDataPath, "masterapp.db");
    return $"Data Source={dbPath}";
}

var configuredDb = ResolveMasterDb(builder.Configuration);
var useSqlServer = IsSqlServerConn(configuredDb) && !IsSqliteConn(configuredDb);

// Resolve SQLite path: on Azure App Service use persistent %HOME%/data/ storage;
// on local dev use ContentRootPath/App_Data/.
static string ResolveSqliteForEnvironment(IWebHostEnvironment env)
{
    var azureHome = Environment.GetEnvironmentVariable("HOME");
    var websiteSiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
    if (!string.IsNullOrEmpty(azureHome) && !string.IsNullOrEmpty(websiteSiteName))
    {
        // Azure App Service: %HOME%/data/ is persistent across deployments
        var dataPath = Path.Combine(azureHome, "data", "App_Data");
        Directory.CreateDirectory(dataPath);
        return $"Data Source={Path.Combine(dataPath, "agentportal_masterapp.db")}";
    }
    return ResolveDevSqlite(env.ContentRootPath);
}

var sqliteConn = useSqlServer ? null : ResolveSqliteForEnvironment(builder.Environment);
if (sqliteConn != null)
{
    EnsureSqliteDirectoryExists(sqliteConn);
    EnsureSqliteBackup(sqliteConn);
}

builder.Services.AddDbContext<MasterAppDbContext>(options =>
{
    // Suppress PendingModelChangesWarning in all environments so Migrate() never throws on snapshot drift
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
// AUTH (Microsoft Identity Web + delegated Graph)
// ------------------------------------------------------------
// ✅ Use OpenIdConnectDefaults for interactive MVC login (delegated OAuth)
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
    })
    .EnableTokenAcquisitionToCallDownstreamApi(
        initialScopes: builder.Configuration["Graph:Scopes"]?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                ?? Array.Empty<string>())
    // Delegated Graph client (uses signed-in user tokens)
    .AddMicrosoftGraph(builder.Configuration.GetSection("Graph"))
    .AddInMemoryTokenCaches();

// ------------------------------------------------------------
// APP-ONLY GRAPH CLIENT (ClientSecretCredential) FOR PROVISIONING
// ------------------------------------------------------------
// This is the SECOND lane. It uses Application permissions + admin consent.
// It does NOT depend on the signed-in user.
builder.Services.AddSingleton<GraphServiceClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var tenantId = cfg["AzureAd:TenantId"];
    var clientId = cfg["AzureAd:ClientId"];
    var clientSecret = cfg["AzureAd:ClientSecret"];

    if (string.IsNullOrWhiteSpace(tenantId) ||
        string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        throw new InvalidOperationException(
            "AzureAd config missing. Ensure AzureAd:TenantId, AzureAd:ClientId, AzureAd:ClientSecret are set (ClientSecret must be the SECRET VALUE).");
    }

    var cred = new ClientSecretCredential(tenantId, clientId, clientSecret);

    // IMPORTANT: .default means "use Application permissions granted to this app registration"
    return new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });
});

// Cookie behavior (kept)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.None;

    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;

    options.LoginPath = "/MicrosoftIdentity/Account/SignIn";
    options.LogoutPath = "/MicrosoftIdentity/Account/SignOut";
    options.AccessDeniedPath = "/Access/Denied";
});

builder.Services.AddAuthorization();

var app = builder.Build();

// Keep the schema in sync in every environment so workstation data tables exist after deployment.
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var startupLogger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Startup");
        startupLogger.LogError(ex, "db.Database.Migrate() failed — app will continue but schema may be stale");
    }
}

// ------------------------------------------------------------
// PRODUCTION EXCEPTION HANDLER (kept)
// ------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            var ex = feature?.Error;

            var requestId = context.TraceIdentifier;
            var path = context.Request.Path.ToString();

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProdException");
            if (ex != null)
                logger.LogError(ex, "UNHANDLED EXCEPTION. requestId={RequestId} path={Path}", requestId, path);

            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(
                $"Server error. requestId={requestId}\n" +
                $"Check Azure Log Stream / Application Logs for the full exception.\n");
        });
    });

    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), accelerometer=(), gyroscope=()";

        var csp = "default-src 'self'; " +
                  "img-src 'self' data: blob: https:; " +
                  "style-src 'self' 'unsafe-inline' https:; " +
                  "script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; " +
                  "font-src 'self' data: https:; " +
                  "connect-src 'self' https: wss:; " +
                  "frame-ancestors 'self';";
        headers["Content-Security-Policy-Report-Only"] = csp;
        return Task.CompletedTask;
    });
    await next();
});

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();

// Required for SignalR WebSocket upgrades on Azure App Service
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseRouting();
app.UseCors("TrackingCors");

app.UseRateLimiter();

app.UseCookiePolicy();
app.UseAuthentication();
app.UseMiddleware<FounderImpersonationMiddleware>();
app.UseMiddleware<AssistantResolutionMiddleware>();
app.UseAuthorization();

// Cache prevention (kept)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized ||
        context.Response.StatusCode == StatusCodes.Status403Forbidden)
    {
        app.Logger.LogWarning("Auth failure {Status} path={Path} user={User}",
            context.Response.StatusCode,
            context.Request.Path,
            context.User?.Identity?.Name ?? "anon");
    }
});

// ✅ REQUIRED so /MicrosoftIdentity/Account/... exists
app.MapRazorPages();

app.MapControllers();

// Explicit route for assistant workspace (helps when custom rewrite rules are present)
app.MapControllerRoute(
    name: "assistant",
    pattern: "Assistant/{action=Index}/{id?}",
    defaults: new { controller = "Assistant", action = "Index" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<LiveSyncHub>("/livesync");
app.MapHub<LeadBridgeHub>("/leadbridgehub");

app.Run();
