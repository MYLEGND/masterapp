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


    [HttpGet("test-create")]
    public async Task<IActionResult> TestCreate(CancellationToken cancellationToken)
    {
        var businessId = "LEGEND@mylegnd.com";
        var serviceId = "124c500f-dce7-4640-b038-b99b724a5a2c"; // 15-Min Call

        var start = DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(18);
        var end = start.AddMinutes(15);

        var appointment = new Microsoft.Graph.Models.BookingAppointment
        {
            ServiceId = serviceId,

            StartDateTime = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = "UTC"
            },

            EndDateTime = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = "UTC"
            },

            CustomerName = "CRM Test User",
            CustomerEmailAddress = "zac.owen+mstest@mylegnd.com",
            CustomerPhone = "5555555555",

            Customers = new List<Microsoft.Graph.Models.BookingCustomerInformationBase>
            {
                new Microsoft.Graph.Models.BookingCustomerInformation
                {
                    Name = "CRM Test User",
                    EmailAddress = "zac.owen+mstest@mylegnd.com",
                    Phone = "5555555555",
                    TimeZone = "America/Phoenix"
                }
            }
        };

        var result = await _graph
            .Solutions
            .BookingBusinesses[businessId]
            .Appointments
            .PostAsync(appointment, cancellationToken: cancellationToken);

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

        return Ok(new
        {
            value = result?.Value?.Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.EmailAddress,
                x.Role,
                x.UseBusinessHours,
                x.AvailabilityIsAffectedByPersonalCalendar,
                x.WorkingHours,
                x.TimeZone
            }).ToList()
        });
    }

}
