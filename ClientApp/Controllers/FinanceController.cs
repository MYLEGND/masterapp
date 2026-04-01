using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClientApp.Services;

namespace ClientApp.Controllers
{
    [Authorize]
    public class FinanceController : Controller
    {
        private readonly EffectiveClientContextService _clientContext;

        public FinanceController(EffectiveClientContextService clientContext)
        {
            _clientContext = clientContext;
        }

        // /Finance (generic view; won't persist without a clientProfileId)
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            ViewData["Title"] = "Finance";
            ViewBag.ClientProfileId = context.ClientProfileId;
            ViewBag.ClientUserId = context.ClientUserId;
            return View();
        }

        // /Finance/Client/123 (Bookkeeping-style: shared finance for a specific client)
        [HttpGet("/Finance/Client/{clientProfileId:guid}")]
        public async Task<IActionResult> Client(Guid clientProfileId)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null || context.ClientProfileId != clientProfileId)
                return Forbid();

            ViewData["Title"] = "Finance";
            ViewBag.ClientProfileId = clientProfileId;
            ViewBag.ClientUserId = context.ClientUserId;
            return View("Index");
        }
    }
}
