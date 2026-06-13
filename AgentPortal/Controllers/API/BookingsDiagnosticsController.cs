using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;

namespace AgentPortal.Controllers.API;

[Authorize]
[ApiController]
[Route("api/bookings-diagnostics")]
public sealed class BookingsDiagnosticsController : ControllerBase
{
    private readonly GraphServiceClient _graph;

    public BookingsDiagnosticsController(GraphServiceClient graph)
    {
        _graph = graph;
    }

    [HttpGet("businesses")]
    public async Task<IActionResult> Businesses(CancellationToken cancellationToken)
    {
        var result = await _graph.Solutions.BookingBusinesses.GetAsync(cancellationToken: cancellationToken);

        return Ok(new
        {
            value = result?.Value?.Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.Email,
                x.WebSiteUrl,
                x.BusinessType
            }).ToList()
        });
    }
    [HttpGet("businesses/{businessId}/services")]
    public async Task<IActionResult> Services(string businessId, CancellationToken cancellationToken)
    {
        var result = await _graph.Solutions.BookingBusinesses[businessId].Services.GetAsync(cancellationToken: cancellationToken);

        return Ok(new
        {
            value = result?.Value?.Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.DefaultDuration,
                x.IsHiddenFromCustomers
            }).ToList()
        });
    }


    [HttpGet("businesses/{businessId}/services/{serviceId}")]
    public async Task<IActionResult> Service(string businessId, string serviceId, CancellationToken cancellationToken)
    {
        var result = await _graph
            .Solutions
            .BookingBusinesses[businessId]
            .Services[serviceId]
            .GetAsync(cancellationToken: cancellationToken);

        return Ok(result);
    }


    [HttpGet("businesses/{businessId}/staff")]
    public async Task<IActionResult> Staff(string businessId, CancellationToken cancellationToken)
    {
        var result = await _graph
            .Solutions
            .BookingBusinesses[businessId]
            .StaffMembers
            .GetAsync(cancellationToken: cancellationToken);

        var staff = new List<object>();

        foreach (var member in result?.Value ?? new List<Microsoft.Graph.Models.BookingStaffMemberBase>())
        {
            if (string.IsNullOrWhiteSpace(member.Id))
                continue;

            var full = await _graph
                .Solutions
                .BookingBusinesses[businessId]
                .StaffMembers[member.Id]
                .GetAsync(cancellationToken: cancellationToken);

            staff.Add(full ?? member);
        }

        return Ok(new
        {
            value = staff
        });
    }

}
