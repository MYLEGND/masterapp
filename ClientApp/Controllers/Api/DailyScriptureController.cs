using Infrastructure.DailyScripture;
using Microsoft.AspNetCore.Mvc;

namespace ClientApp.Controllers.Api;

[ApiController]
[Route("api/shared/daily-scripture")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DailyScriptureController : ControllerBase
{
    private readonly IDailyScriptureService _scripture;

    public DailyScriptureController(IDailyScriptureService scripture) => _scripture = scripture;

    [HttpGet]
    public ActionResult<DailyScriptureResponse> Get()
    {
        var daily = _scripture.GetTodayUtc();
        return Ok(new DailyScriptureResponse(
            daily.Date,
            daily.Reference,
            daily.Translation,
            daily.Verses,
            daily.Text));
    }
}

public sealed record DailyScriptureResponse(
    string Date,
    string Reference,
    string Translation,
    IReadOnlyList<string> Verses,
    string Text);
