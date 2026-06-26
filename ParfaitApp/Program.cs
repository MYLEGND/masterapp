using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ParfaitApp.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

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
builder.Services.AddScoped<IGraphMailService, GraphMailService>();
builder.Services.AddSingleton<ParfaitMetaCapiCredentialProtector>();
builder.Services.AddScoped<IParfaitBusinessProfileService, ParfaitBusinessProfileService>();
builder.Services.AddScoped<IParfaitMetaAdsOAuthService, ParfaitMetaAdsOAuthService>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "ParfaitApp.InternalAuth";
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

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

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
