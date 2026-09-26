using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Controllers;

/// <summary>
/// Protect hosts the shared MasterApp website platform HTTP surface.
/// All website management, publication and runtime authority lives in Infrastructure.
/// </summary>
[ApiController]
[Route("api/website-content")]
public sealed class WebsiteContentController : WebsitePlatformController
{
    public WebsiteContentController(
        MasterAppDbContext db,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration)
        : base(db, tickets, configuration)
    {
    }
}
