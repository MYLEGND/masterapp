using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ParfaitApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();


builder.Services.AddScoped<IParfaitBusinessProfileService, ParfaitBusinessProfileService>();

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

        options.Events.OnTokenValidated = context =>
        {
            var email =
                context.Principal?.FindFirst("preferred_username")?.Value ??
                context.Principal?.FindFirst("email")?.Value ??
                "";

            if (!email.EndsWith("@mylegnd.com", StringComparison.OrdinalIgnoreCase))
            {
                context.Fail("Only @mylegnd.com accounts can access Parfait internal.");
            }

            return Task.CompletedTask;
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
