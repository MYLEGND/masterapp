using Infrastructure.Data;
using ClientApp.Services;
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
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<EffectiveClientContextService>();
builder.Services.AddScoped<IAzureUserUpdater, NoopAzureUserUpdater>();
builder.Services.AddDataProtection().SetApplicationName("MasterApp.ClientApp");

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
var clientSecret = builder.Configuration["AzureAd:ClientSecret"]; // may be null

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

    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
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

            return Task.CompletedTask;
        },

        // Return 401 for AJAX requests instead of redirecting to Azure AD
        OnRedirectToIdentityProvider = ctx =>
        {
            if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
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

// ------------------------------------------------------------
// HARD PRODUCTION EXCEPTION HANDLER (CAN'T FAIL)
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

// ------------------------------------------------------------
// ALWAYS LOG UNHANDLED (DEV + PROD) WITHOUT SWALLOWING
// ------------------------------------------------------------
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalException");
        logger.LogError(ex, "Unhandled exception on {Path}", context.Request.Path);
        throw;
    }
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

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

    if (string.IsNullOrWhiteSpace(clientSecret))
        logger.LogError("AzureAd:ClientSecret is missing. Set AzureAd__ClientSecret in Azure App Settings.");

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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"
);

app.Run();
