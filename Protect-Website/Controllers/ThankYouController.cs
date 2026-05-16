using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProtectWebsite.Services.Meta;
using Shared.Meta;

namespace Protect_Website.Controllers
{
    public class ThankYouController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly ILogger<ThankYouController> _logger;

        public ThankYouController(MasterAppDbContext db, ILogger<ThankYouController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // GET: /ThankYou
        public IActionResult Index()
        {
            // Loads Views/Quote/ThankYou.cshtml
            return View("~/Views/Quote/ThankYou.cshtml");
        }

        [HttpPost("/ThankYou/meta-browser-ack")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> AckBrowserPixel([FromBody] ThankYouMetaBrowserPixelAckRequest? request)
        {
            if (request == null ||
                request.LeadId == Guid.Empty ||
                string.IsNullOrWhiteSpace(request.EventId))
            {
                return BadRequest(new { error = "Invalid browser pixel acknowledgment." });
            }

            var lead = await _db.WebsiteLeads.FirstOrDefaultAsync(
                x => x.LeadId == request.LeadId,
                HttpContext?.RequestAborted ?? CancellationToken.None);

            if (lead == null)
                return NotFound();

            var currentState = MetaLeadTrackingJson.Read(lead.MetadataJson);
            if (!string.IsNullOrWhiteSpace(currentState?.EventId) &&
                !string.Equals(currentState.EventId, request.EventId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Conflict();
            }

            var normalizedStatus = MetaLeadTrackingWorkflow.NormalizeBrowserPixelStatus(request.Status);
            var normalizedNote = MetaLeadTrackingWorkflow.NormalizeBrowserPixelNote(request.Note);

            lead.MetadataJson = MetaLeadTrackingJson.Upsert(
                lead.MetadataJson,
                state =>
                {
                    state.EventId ??= request.EventId.Trim();
                    state.BrowserPixelStatus = normalizedStatus;
                    state.BrowserPixelUpdatedUtc = DateTime.UtcNow;
                    state.BrowserPixelNote = normalizedNote;
                });

            try
            {
                await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
                _logger.LogInformation(
                    "ThankYou browser pixel ack lead={LeadId} status={Status} eventId={EventId}",
                    request.LeadId, normalizedStatus, request.EventId);
            }
            catch (Exception ackEx)
            {
                _logger.LogError(
                    ackEx,
                    "ThankYou browser pixel ack save failed lead={LeadId} status={Status} eventId={EventId}",
                    request.LeadId, normalizedStatus, request.EventId);
            }

            return NoContent();
        }
    }
}
