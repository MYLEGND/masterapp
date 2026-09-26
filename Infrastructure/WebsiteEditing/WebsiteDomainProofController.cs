using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing.Controllers;

/// <summary>
/// Public custom-domain ownership proof. Kept independent from editor ticket
/// infrastructure so pending bindings can be verified before the website is active.
/// </summary>
public sealed class WebsiteDomainProofController(
    MasterAppDbContext db,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("/.well-known/legend-website")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DomainProof(CancellationToken cancellationToken = default)
    {
        var host = WebsiteRequestHostResolver.Resolve(HttpContext, configuration);
        var binding = await db.Set<WebsiteDomainBinding>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Hostname == host && candidate.Status != "removing",
                cancellationToken);

        if (binding is null ||
            !await db.CommerceBusinesses.AnyAsync(
                business =>
                    business.Id == binding.CommerceBusinessId &&
                    business.IsActive &&
                    business.Status == "Active",
                cancellationToken))
            return NotFound();

        return Ok(new
        {
            businessId = binding.CommerceBusinessId,
            bindingId = binding.Id
        });
    }
}
