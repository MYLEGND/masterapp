using System.Security.Claims;
using AgentPortal.Security;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

[CollectionDefinition("Profile website Founder environment", DisableParallelization = true)]
public class ProfileWebsiteFounderEnvironment { }

[Collection("Profile website Founder environment")]
public class ProfileWebsiteFounderTests : System.IDisposable
{
    private const string FounderId = "22222222-2222-2222-2222-222222222222";
    private readonly string? previousFounder = System.Environment.GetEnvironmentVariable("FOUNDER_OID");
    public ProfileWebsiteFounderTests() => System.Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);
    public void Dispose() => System.Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);

    [Fact]
    public void GlobalLegendEdit_DeniesRegularAgent()
    {
        var controller = Build("agent@example.test");
        Assert.IsType<ForbidResult>(controller.EditLegendWebsite());
    }

    [Fact]
    public void GlobalLegendEdit_RestoresFounderRoute()
    {
        var controller = Build(FounderGuard.FounderEmail, FounderId);
        var result = Assert.IsType<RedirectResult>(controller.EditLegendWebsite());
        Assert.StartsWith("https://www.example.test/?legendEdit=", result.Url);
    }

    [Fact]
    public async System.Threading.Tasks.Task GlobalLegendSession_DeniesRegularAgentAndUnknownScope()
    {
        var controller = Build("agent@example.test");
        Assert.IsType<ForbidResult>(await controller.WebsiteSession("legend"));
        Assert.IsType<BadRequestResult>(await controller.WebsiteSession("business"));
    }

    private static AccountController Build(string email, string actor = "11111111-1111-1111-1111-111111111111")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?> { ["LegendWebsiteBaseUrl"] = "https://www.example.test" }).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        return new AccountController(null!, null!, null!, null!, new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider()), null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", actor), new Claim(ClaimTypes.Email, email) }, "Test"))
            } }
        };
    }
}
