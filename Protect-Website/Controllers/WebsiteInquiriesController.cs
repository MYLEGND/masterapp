using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using ProtectWebsite.Services.Communication;

namespace ProtectWebsite.Controllers;

[ApiController]
[Route("api/website-inquiries")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WebsiteInquiriesController : WebsiteInquiryAuthority
{
    public WebsiteInquiriesController(MasterAppDbContext db, WebsiteEditorTicketProtector tickets,
        IConfiguration configuration, PublicWebsiteRuntimeScopeResolver publicScopes,
        IWebsiteLifeLeadCaptureService capture, WebsiteIntakeRecipientResolver recipients,
        IProtectEmailSender emailSender)
        : base(db, tickets, configuration, publicScopes, capture, recipients, emailSender) { }
}
