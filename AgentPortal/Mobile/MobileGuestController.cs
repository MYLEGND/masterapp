using Infrastructure.DailyScripture;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Mobile;

// Public reading content only. Guest browsing creates no actor, account,
// token, entitlement, profile, or access to the authenticated mobile shell.
[ApiController]
[Route("api/v1/mobile/guest")]
[AllowAnonymous]
public sealed class MobileGuestController(IDailyScriptureService scripture) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<MobileGuestSnapshot>> Get(CancellationToken cancellationToken)
    {
        var today = scripture.GetBusinessDate(DateTime.UtcNow);
        var readings = new List<MobileGuestReading>();
        for (var offset = 0; offset < 7; offset++)
        {
            var date = today.AddDays(-offset);
            var reading = await scripture.GetForDateAsync(date, cancellationToken);
            readings.Add(new(date.ToString("yyyy-MM-dd"), reading.Reference, reading.Translation, reading.Text));
        }
        return Ok(new MobileGuestSnapshot(
            "Explore Legend",
            "A little inspiration. A clearer next step.",
            "Read, reflect, and get to know Legend. No account is needed for this space.",
            readings,
            [
                new("getting-started", "Getting started", "Your first steps with Legend",
                    "Explore the daily readings at your own pace. When you are ready to use your client account, choose Sign in securely and use the account connected to your agent. If your agent gave you access instructions, follow those instructions on the sign-in screen."),
                new("your-connection", "Working with your agent", "Keep your conversations together",
                    "Your signed-in workspace connects you with your agent. It keeps account conversations and appointments tied to your account, so you can return to them on your supported devices. Contact your agent if you need help finding the right account."),
                new("your-privacy", "Your private workspace", "Know what stays behind sign-in",
                    "Guest browsing does not create an account. Messages, client records, financial reporting, activities, appointments, reminders, and profile settings require secure sign-in and the appropriate account access. Guest browsing never asks to connect your contacts, calendar, or reminders.")
            ],
            "Your account, when you are ready",
            "Sign in to access the private features available to your client or agent account.",
            [new("Privacy policy", "https://protect.mylegnd.com/Privacy"),
             new("Terms of use", "https://protect.mylegnd.com/Terms")]));
    }
}

public sealed record MobileGuestSnapshot(string Title, string Subtitle, string Introduction,
    IReadOnlyList<MobileGuestReading> Readings, IReadOnlyList<MobileGuestGuide> Guides,
    string AccountTitle, string AccountDescription, IReadOnlyList<MobileGuestLink> Links);
public sealed record MobileGuestReading(string Date, string Reference, string Translation, string Text);
public sealed record MobileGuestGuide(string Id, string Title, string Subtitle, string Text);
public sealed record MobileGuestLink(string Title, string Url);
