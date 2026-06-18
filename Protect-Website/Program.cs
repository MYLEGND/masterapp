using ProtectWebsite.Services.Communication;
using Azure.Identity;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services.Booking;
using System.IO;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Enable app-level logs in Azure log stream so Meta CAPI send/skip/fail results are visible.
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// 🔹 Azure Key Vault — pulls secrets (e.g. AzureAd:ClientSecret) at startup
//    Works locally (via az login / VS Code Azure account) and in production (via Managed Identity).
var keyVaultUri = builder.Configuration["KeyVault:Uri"]
    ?? "https://masterapp-kv-1221.vault.azure.net/";
builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());

// 🔹 MVC
var mvcBuilder = builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<ProtectWebsite.Services.Tracking.TrackingViewDataFilter>();
});
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// DbContext for tracking resolution
static bool IsSqlServerConn(string? cs) =>
    !string.IsNullOrWhiteSpace(cs) &&
    (cs.Contains("Server=tcp:", StringComparison.OrdinalIgnoreCase) ||
     cs.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
     cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase));

var connString = builder.Configuration.GetConnectionString("MasterAppDb")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb");

if (string.IsNullOrWhiteSpace(connString))
{
    throw new InvalidOperationException("ConnectionStrings:MasterAppDb is required for tracking resolution.");
}


if (builder.Environment.IsDevelopment() &&
    connString.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(Environment.GetEnvironmentVariable("ALLOW_PROD_DB_LOCAL"), "true", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Refusing to run Protect-Website locally against Azure SQL. Use local SQLite or set ALLOW_PROD_DB_LOCAL=true intentionally.");
}

if (IsSqlServerConn(connString))
{
    builder.Services.AddDbContext<Infrastructure.Data.MasterAppDbContext>(opts =>
        opts.UseSqlServer(connString));
}
else
{
    builder.Services.AddDbContext<Infrastructure.Data.MasterAppDbContext>(opts =>
        opts.UseSqlite(connString));
}

builder.Services.AddScoped<IProtectEmailSender, GraphProtectEmailSender>();

builder.Services.AddScoped<ProtectWebsite.Services.Tracking.AgentTrackingResolver>();
builder.Services.AddScoped<ProtectWebsite.Services.Tracking.SlugRoutingMiddleware>();
builder.Services.AddScoped<IWebsiteLifeLeadCaptureService, WebsiteLifeLeadCaptureService>();
builder.Services.AddScoped<IMetaPixelResolutionService, MetaPixelResolutionService>();
builder.Services.AddScoped<IMetaSignalIntelligenceService, MetaSignalIntelligenceService>();
builder.Services.Configure<PublicBookingOptions>(builder.Configuration.GetSection("PublicBooking"));
builder.Services.AddScoped<IPublicBookingResolver, PublicBookingResolver>();
builder.Services.AddScoped<IPublicBookingCalendarMatcher, MicrosoftGraphPublicBookingCalendarMatcher>();
builder.Services.AddScoped<IPublicBookingConfirmationService, PublicBookingConfirmationService>();
builder.Services.AddSingleton<IPublicBookingContextProtector, PublicBookingContextProtector>();
builder.Services.AddSingleton<MetaCapiCredentialProtector>();
builder.Services.Configure<MetaOptions>(builder.Configuration.GetSection("Meta"));
builder.Services.Configure<MetaSignalIntelligenceOptions>(builder.Configuration.GetSection("MetaSignalIntelligence"));
builder.Services.AddHttpClient<IMetaConversionsApiService, MetaConversionsApiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<MetaSignalAnalyticsBridge>();
builder.Services.AddHostedService<MetaSignalOutcomeDispatcherHostedService>();

var dpBlobUri = builder.Configuration["DataProtection:BlobUri"];
var dpKeyVaultId = builder.Configuration["DataProtection:KeyVaultKeyId"];

var dataProtectionBuilder = builder.Services.AddDataProtection()
    // Shares the same key ring/app isolation as AgentPortal so protected
    // agent-scoped Meta CAPI credentials can be decrypted safely here.
    .SetApplicationName("AgentPortal");

if (!string.IsNullOrWhiteSpace(dpBlobUri) && !string.IsNullOrWhiteSpace(dpKeyVaultId))
{
    var azureCred = new DefaultAzureCredential();
    dataProtectionBuilder
        .PersistKeysToAzureBlobStorage(new Uri(dpBlobUri), azureCred)
        .ProtectKeysWithAzureKeyVault(new Uri(dpKeyVaultId), azureCred);
}
else
{
    var sharedKeysDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "AgentPortal", "App_Data", "keys"));
    Directory.CreateDirectory(sharedKeysDir);
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(sharedKeysDir));
}

// 🔹 🔹 SESSION SUPPORT for TempData
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

if (!IsSqlServerConn(connString))
{
    using var scope = app.Services.CreateScope();
    var startupLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");
    var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Data.MasterAppDbContext>();
    await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, startupLogger, app.Lifetime.ApplicationStopping);
}

if (app.Environment.IsDevelopment())
{
    var analyticsValidatorLogger = app.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("AnalyticsPipelineValidator");
    // Attempt to locate and invoke AnalyticsPipelineValidator.LogWarnings via reflection so compilation
    // succeeds even if the type/assembly isn't present.
    var validatorType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("ProtectWebsite.Services.Tracking.AnalyticsPipelineValidator"))
        .FirstOrDefault(t => t != null);
    if (validatorType != null)
    {
        var method = validatorType.GetMethod("LogWarnings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (method != null)
        {
            try
            {
                method.Invoke(null, new object[] { app.Environment.ContentRootPath, analyticsValidatorLogger });
            }
            catch (Exception ex)
            {
                analyticsValidatorLogger.LogWarning(ex, "AnalyticsPipelineValidator invocation failed.");
            }
        }
        else
        {
            analyticsValidatorLogger.LogDebug("AnalyticsPipelineValidator.LogWarnings not found.");
        }
    }
    else
    {
        analyticsValidatorLogger.LogDebug("AnalyticsPipelineValidator type not found; skipping analytics validation.");
    }
}

// 🔹 Middleware
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
// Agent slug routing / context must run before routing so rewritten paths are routed correctly
app.UseMiddleware<ProtectWebsite.Services.Tracking.SlugRoutingMiddleware>();

app.UseStaticFiles();
app.UseRouting();

// 🔹 Enable session BEFORE MVC
app.UseSession();

// 🔹 Optional: Cache prevention for public site
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

// 🔹 Default MVC route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();

// 🔹 SAFELY set Azure port (does NOT break your email logic)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    app.Urls.Clear();
    app.Urls.Add($"http://*:{port}");
}

app.Run();
