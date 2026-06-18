using AgentPortal.Hubs;
using AgentPortal.Middleware;
using AgentPortal.Services;
using Azure.Identity;
using Infrastructure.Data;
using Infrastructure.Identity;
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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using LegendApp.Services.Budget;
using LegendApp.Services.Budget.Interfaces;
using Microsoft.Identity.Web.UI;
using QuestPDF.Infrastructure;
using System.Threading.RateLimiting;
using System.IO;
using System.Linq;

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

// Feature flags (all default false; override via configuration)
builder.Services.Configure<AgentPortal.Models.AppFeatureFlags>(builder.Configuration.GetSection("Features"));
builder.Services.Configure<AgentPortal.Models.Analytics.LandingRoutesOptions>(builder.Configuration.GetSection("LandingRoutes"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FounderOnly", policy =>
    {
        policy.RequireAssertion(ctx => FounderGuard.IsFounder(ctx.User));
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientProvisioningService>();
builder.Services.AddScoped<IAzureClientEmailSyncService, AzureClientEmailSyncService>();
builder.Services.AddScoped<AssistantContextService>();
builder.Services.AddScoped<AgentRegistryService>();
builder.Services.AddScoped<AgencyCommandService>();
builder.Services.AddScoped<FounderImpersonationService>();
builder.Services.AddScoped<ProductionService>();
builder.Services.AddScoped<MetaSignalCrmOutcomeService>();
builder.Services.AddScoped<EffectiveAgentContext>();
builder.Services.AddScoped<IAdvancedMarketsCalculationService, AdvancedMarketsCalculationService>();
builder.Services.AddSingleton<IAgentTimeZoneResolver, AgentTimeZoneResolver>();
builder.Services.AddSingleton<IBudgetCalculator, BudgetCalculator>();
var redisConn = builder.Configuration["SignalR:RedisConnectionString"];
// LeadBridge state: Redis-backed (multi-instance ready) when Redis is configured; in-memory fallback for local dev
if (!string.IsNullOrWhiteSpace(redisConn))
    builder.Services.AddSingleton<ILeadBridgeStateService, DistributedLeadBridgeStateService>();
else
    builder.Services.AddSingleton<ILeadBridgeStateService, LeadBridgeStateService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<IAnalyticsIncidentQueryService, AnalyticsIncidentQueryService>();
builder.Services.AddScoped<IMetaSignalAnalyticsService, MetaSignalAnalyticsService>();
builder.Services.AddSingleton<ILandingRouteDiscoveryService, LandingRouteDiscoveryService>();
builder.Services.AddScoped<AgentPortal.Services.Analytics.WebsiteAnalyticsAiDataBuilder>();
builder.Services.AddScoped<AgentPortal.Services.Analytics.IVisitorConcentrationService, AgentPortal.Services.Analytics.VisitorConcentrationService>();
builder.Services.AddScoped<AgentPortal.Services.Analytics.IKpiDetailBreakdownService, AgentPortal.Services.Analytics.KpiDetailBreakdownService>();
builder.Services.AddScoped<AgentPortal.Services.Analytics.IVisitorTrustScoringService, AgentPortal.Services.Analytics.VisitorTrustScoringService>();
builder.Services.AddScoped<AgentPortal.Services.Analytics.OpenAiWebsiteAnalyticsReviewService>();
builder.Services.AddHttpClient("OpenAI", c =>
{
    // IsNullOrWhiteSpace so an empty string in config ("BaseUrl": "") falls through
    // to the default instead of producing a schemeless URI that resolves to file:///.
    var configuredBase = builder.Configuration["OpenAI:BaseUrl"];
    var resolvedBase = !string.IsNullOrWhiteSpace(configuredBase)
        ? configuredBase.TrimEnd('/') + "/"
        : "https://api.openai.com/";
    c.BaseAddress = new Uri(resolvedBase);
    // Timeout is slightly longer than the service-level timeout (OpenAI:TimeoutSeconds)
    // so the service's CancellationTokenSource fires first and returns a clean error.
    var svcTimeout = int.TryParse(builder.Configuration["OpenAI:TimeoutSeconds"], out var st) && st > 0 ? st : 30;
    c.Timeout = TimeSpan.FromSeconds(svcTimeout + 5);
});
// Warn at startup if OpenAI key is missing — non-fatal; AI features simply return error results
if (!AgentPortal.Services.Analytics.OpenAiKeyResolver.IsConfigured(builder.Configuration))
{
    Console.WriteLine("[WARN] OpenAI API key is not configured. AI insights features will return error results until a key is set via OpenAI:ApiKey (config) or the OPENAI_API_KEY environment variable.");
}
builder.Services.AddScoped<IMetaAdsService, MetaAdsService>();
builder.Services.AddScoped<IMetaAdsConnectionStore, MetaAdsConnectionStore>();
builder.Services.AddScoped<IMetaAdsOAuthService, MetaAdsOAuthService>();
builder.Services.AddScoped<IAgentTrackingService, AgentTrackingService>();
builder.Services.AddScoped<AgentTrackingProvisioningFilter>();
builder.Services.AddScoped<AgentTrackingResolver>();
builder.Services.AddScoped<IExecutionEngine, ExecutionEngine>();
builder.Services.AddScoped<IDecisionService, DecisionService>();
builder.Services.AddScoped<IBlockerService, BlockerService>();
builder.Services.AddScoped<ICommitmentService, CommitmentService>();
builder.Services.AddScoped<IPlaybookEngine, PlaybookEngine>();
builder.Services.AddHostedService<MigrationHealthHostedService>();
builder.Services.AddHostedService<AnalyticsIncidentResponseHostedService>();
builder.Services.AddHostedService<GraphCalendarSubscriptionHostedService>();
builder.Services.AddHostedService<LeadAppointmentAutoCompletionHostedService>();
builder.Services.AddHostedService<AzureAgentDirectorySyncHostedService>();
builder.Services.AddSingleton<MetaCapiCredentialProtector>();
builder.Services.AddSingleton<PiiProtector>();
builder.Services.AddSingleton<IngestSignatureValidator>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AgentPortal.Services.ImportValidation.LeadImportValidator>();
builder.Services.AddScoped<DerivedAnalyticsService>();
builder.Services.AddHttpClient("ResilientDefault")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
var hcBuilder = builder.Services.AddHealthChecks()
    .AddCheck<DbReadinessCheck>("db", tags: ["ready"]);
// Redis health check registered only when Redis is configured (redisConn declared above)
if (!string.IsNullOrWhiteSpace(redisConn))
    hcBuilder.AddCheck<RedisReadinessCheck>("redis", tags: ["ready"]);
hcBuilder.AddCheck<AgentPortal.Health.LiveSyncPingHealthCheck>("livesync", tags: ["ready"]);
hcBuilder.AddCheck<AgentPortal.Health.IngestHealthCheck>("ingest", tags: ["ready"]);
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var inner = sp.GetRequiredService<SmtpEmailSender>();
    var logger = sp.GetRequiredService<ILogger<AgentPortal.Services.Resilience.ResilientEmailSender>>();
    var flags = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags>>();
    return new AgentPortal.Services.Resilience.ResilientEmailSender(inner, logger, flags);
});
// Application Insights telemetry — only registered when connection string is present
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddApplicationInsightsTelemetry();

var signalRBuilder = builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 64 * 1024; // 64 KB per message — guard against oversized payloads
});
if (!string.IsNullOrWhiteSpace(redisConn))
    signalRBuilder.AddStackExchangeRedis(redisConn);

// Distributed cache: Redis when available; in-memory fallback for local dev
if (!string.IsNullOrWhiteSpace(redisConn))
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConn);
else
    builder.Services.AddDistributedMemoryCache();

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
// ------------------------------------------------------------
// DATA PROTECTION KEY PERSISTENCE
// Production: keys persisted to Azure Blob Storage, protected by Azure Key Vault.
// Local dev:  keys persisted to local filesystem (fallback when Azure config absent).
//
// Required Azure App Service settings for production:
//   DataProtection__BlobUri     — full URI to the blob, e.g.
//                                 https://<account>.blob.core.windows.net/<container>/keys.xml
//   DataProtection__KeyVaultKeyId — Key Vault key identifier URI, e.g.
//                                 https://<vault>.vault.azure.net/keys/<keyname>
//
// The App Service Managed Identity must have:
//   - Storage Blob Data Contributor on the storage account (or the container)
//   - Key Vault Crypto User on the Key Vault key
// ------------------------------------------------------------
var dpBlobUri    = builder.Configuration["DataProtection:BlobUri"];
var dpKeyVaultId = builder.Configuration["DataProtection:KeyVaultKeyId"];

var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("AgentPortal");

if (!string.IsNullOrWhiteSpace(dpBlobUri) && !string.IsNullOrWhiteSpace(dpKeyVaultId))
{
    // Production path: Azure Blob + Key Vault via Managed Identity
    var azureCred = new Azure.Identity.DefaultAzureCredential();
    dataProtectionBuilder
        .PersistKeysToAzureBlobStorage(new Uri(dpBlobUri), azureCred)
        .ProtectKeysWithAzureKeyVault(new Uri(dpKeyVaultId), azureCred);
}
else
{
    // Local dev fallback: filesystem keys under App_Data/keys
    var localKeysDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
    Directory.CreateDirectory(localKeysDir);
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(localKeysDir));
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ingest", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
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
    // Azure App Service injects Connection Strings as SQLCONNSTR_<Name>.
    // Check environment first so appsettings SQLite defaults never override production SQL.
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
var isAzureAppService = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
var useSqlServerInDevelopment =
    !string.Equals(
        builder.Configuration["Database:UseSqlServerInDevelopment"],
        "false",
        StringComparison.OrdinalIgnoreCase)
    || string.Equals(
        Environment.GetEnvironmentVariable("MASTERAPP_DEV_USE_SQLSERVER"),
        "true",
        StringComparison.OrdinalIgnoreCase);

if (builder.Environment.IsDevelopment() &&
    !isAzureAppService &&
    useSqlServer &&
    !useSqlServerInDevelopment)
{
    Console.WriteLine(
        "[INFO] Development environment detected with a SQL Server connection string available. " +
        "Using SQL Server as the default local provider. " +
        "Set Database:UseSqlServerInDevelopment=false only if you intentionally want SQLite locally.");
    useSqlServer = false;
}

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

// ── Founder / owner identity resolution ──────────────────────────────────
// Primary:   OWNER_EMAIL / FOUNDER_OID  Azure App Service Application Settings (env vars)
// Secondary: Founder:Email / Founder:Oid  appsettings.Production.json config keys
// Sets env vars so that OnboardingGuard / FounderGuard static fields pick them up
// on first use (which happens per-request, after this startup block runs).
{
    var resolvedOwnerEmail =
        Environment.GetEnvironmentVariable("OWNER_EMAIL")
        ?? Environment.GetEnvironmentVariable("OwnerEmail")
        ?? builder.Configuration["Founder:Email"]?.Trim();

    if (!string.IsNullOrWhiteSpace(resolvedOwnerEmail))
        Environment.SetEnvironmentVariable("OWNER_EMAIL", resolvedOwnerEmail);

    var resolvedFounderOid =
        Environment.GetEnvironmentVariable("FOUNDER_OID")
        ?? Environment.GetEnvironmentVariable("FounderOid")
        ?? builder.Configuration["Founder:Oid"]?.Trim();

    if (!string.IsNullOrWhiteSpace(resolvedFounderOid))
        Environment.SetEnvironmentVariable("FOUNDER_OID", resolvedFounderOid);
}

// PRODUCTION GUARD: owner email must resolve from at least one source.
var ownerEmail = Environment.GetEnvironmentVariable("OWNER_EMAIL");
if (string.IsNullOrWhiteSpace(ownerEmail) &&
    string.Equals(builder.Environment.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "STARTUP BLOCKED: Owner email is required in Production. " +
        "Set OWNER_EMAIL in Azure App Service → Configuration → Application settings, " +
        "or add Founder:Email to appsettings.Production.json.");
}

// PRODUCTION GUARD: refuse to start on SQLite when running on Azure App Service.
// If WEBSITE_SITE_NAME is set, we are in a hosted/deployed environment and must have
// a SQL Server connection string. A missing or misconfigured connection string must
// produce a hard startup failure, not a silent SQLite fallback.
if (!useSqlServer && isAzureAppService)
{
    throw new InvalidOperationException(
        "STARTUP BLOCKED: Deployed to Azure App Service but no SQL Server connection string was resolved. " +
        "Set the 'MasterAppDb' Connection String (type: SQLServer) in Azure Portal → App Service → " +
        "Configuration → Connection strings. The app will not start on SQLite in a hosted environment.");
}

if (sqliteConn != null)
{
    EnsureSqliteDirectoryExists(sqliteConn);
    EnsureSqliteBackup(sqliteConn);
}

// Migration strictness is opt-in.
// When enabled, startup will hard-stop on pending migrations and pending model changes.
// Leave disabled when the priority is app availability over schema enforcement.
var strictMigrations = string.Equals(builder.Configuration["Migrations:Strict"], "true", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("MIGRATION_STRICT"), "true", StringComparison.OrdinalIgnoreCase);

builder.Services.AddDbContext<MasterAppDbContext>(options =>
{
    // In strict environments, treat model drift as fatal; in local/dev keep warnings noisy but non-blocking.
    options.ConfigureWarnings(w =>
    {
        if (strictMigrations)
            w.Throw(RelationalEventId.PendingModelChangesWarning);
        else
            w.Ignore(RelationalEventId.PendingModelChangesWarning);
    });

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

    var tenantId = cfg["GraphProvisioning:TenantId"]
                   ?? cfg["GraphProvisioning__TenantId"]
                   ?? cfg["AzureAd:TenantId"]
                   ?? cfg["AzureAd__TenantId"];

    var clientId = cfg["GraphProvisioning:ClientId"]
                  ?? cfg["GraphProvisioning__ClientId"]
                  ?? cfg["AzureAd:ClientId"]
                  ?? cfg["AzureAd__ClientId"];

    var clientSecret = cfg["GraphProvisioning:ClientSecret"]
                      ?? cfg["GraphProvisioning__ClientSecret"]
                      ?? cfg["AzureAd:ClientSecret"]
                      ?? cfg["AzureAd__ClientSecret"];

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

// For OIDC challenges (Graph token expiry), also return 401 for AJAX requests
// instead of redirecting to Azure AD — same reason as the cookie handler below.
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, oidcOptions =>
{
    var original = oidcOptions.Events?.OnRedirectToIdentityProvider;
    oidcOptions.Events ??= new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents();
    oidcOptions.Events.OnRedirectToIdentityProvider = async ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.HandleResponse();
            return;
        }
        if (original != null) await original(ctx);
    };
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

    // For AJAX/fetch calls, return 401 instead of redirecting to Azure AD.
    // Without this, fetch() follows the 302 cross-origin → CORS blocks it.
    options.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
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

builder.Services.AddAuthorization();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:RunMigrationsOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

    try
    {
        db.Database.Migrate();
        Console.WriteLine("SUCCESS: Production migrations applied.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED applying migrations: {ex}");
        throw;
    }
}


// Hard-stop on unapplied migrations in strict environments to prevent schema drift reaching prod.
if (strictMigrations && !builder.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
    var pending = db.Database.GetPendingMigrations().ToList();
    if (pending.Count > 0)
    {
        var preview = string.Join(", ", pending.Take(5));
        throw new InvalidOperationException($"STARTUP BLOCKED: pending EF migrations detected ({pending.Count}). Apply migrations before deploy. First: {preview}");
    }

    // Optional: require Redis for SignalR when the feature flag is enabled
    var flags = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags>>().Value;
    if (flags.SignalRRequireRedis && string.IsNullOrWhiteSpace(builder.Configuration["SignalR:RedisConnectionString"]))
    {
        throw new InvalidOperationException("STARTUP BLOCKED: SignalR Redis connection is required in production when SignalRRequireRedis is enabled.");
    }

    // Emit applied migration signature for observability
    var applied = db.Database.GetAppliedMigrations().ToList();
    app.Logger.LogInformation("Migrations applied: {AppliedCount} latest={LatestMigration}", applied.Count, applied.LastOrDefault() ?? "(none)");
}

// Auto-migrate SQLite (local dev only). SQL Server migrations must be applied explicitly
// via 'dotnet ef database update' before deployment — never at runtime on production.
{
    using var scope = app.Services.CreateScope();
    var startupLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");

    if (useSqlServer)
    {
        startupLogger.LogInformation(
            "SQL Server detected — startup auto-migration is DISABLED. " +
            "Apply pending migrations with 'dotnet ef database update' before deploying.");
    }
    else
    {
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        try
        {
            await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, startupLogger, app.Lifetime.ApplicationStopping);
            startupLogger.LogInformation("SQLite startup schema bootstrap completed successfully.");
        }
        catch (Exception ex)
        {
            startupLogger.LogError(ex, "SQLite startup database initialization failed.");
        }
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
                  "script-src 'self' 'unsafe-inline' https:; " +
                  "font-src 'self' data: https:; " +
                  "connect-src 'self' https: wss:; " +
                  "frame-ancestors 'self';";
        headers["Content-Security-Policy"] = csp;
        return Task.CompletedTask;
    });
    await next();
});

app.UseForwardedHeaders();
// Keep local proxy-to-portal calls simple in Development (Protect-Website -> AgentPortal)
// by allowing HTTP on localhost. HTTPS redirection remains enforced outside Development.
if (!app.Environment.IsDevelopment())
{
app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/api/graph/calendar-webhook")) {
        await next(); return;
    }
    await next();
});
    
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/graph/calendar-webhook") &&
        context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
        context.Request.Query.TryGetValue("validationToken", out var validationToken) &&
        !string.IsNullOrWhiteSpace(validationToken.ToString()))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(validationToken.ToString());
        return;
    }
    await next();
});
app.UseHttpsRedirection();
}
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

// Liveness: always returns 200 if the process is running
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false  // no checks — pure process liveness
}).AllowAnonymous();

// Readiness: includes DB connectivity; used by load balancer before routing traffic
app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Count == 0 || check.Tags.Contains("ready")
}).AllowAnonymous();

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
