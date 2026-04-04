using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 🔹 MVC
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<ProtectWebsite.Services.Tracking.TrackingViewDataFilter>();
});
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

builder.Services.AddScoped<ProtectWebsite.Services.Tracking.AgentTrackingResolver>();
builder.Services.AddScoped<ProtectWebsite.Services.Tracking.SlugRoutingMiddleware>();

// 🔹 🔹 SESSION SUPPORT for TempData
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

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
