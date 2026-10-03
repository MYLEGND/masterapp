using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;

namespace ProtectWebsite.Controllers;

[ApiController]
[Route("api/website-inquiries")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WebsiteInquiriesController : WebsiteInquiryAuthority
{
    public WebsiteInquiriesController(MasterAppDbContext db, WebsiteEditorTicketProtector tickets,
        IConfiguration configuration, PublicWebsiteRuntimeScopeResolver publicScopes,
        IWebsiteLifeLeadCaptureService capture, WebsiteIntakeRecipientResolver recipients,
        IWebsiteInquiryEmailSender emailSender)
        : base(db, tickets, configuration, publicScopes, capture, recipients, emailSender) { }
}
