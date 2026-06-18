
using Microsoft.AspNetCore.Http;
using ProtectWebsite.Services.Tracking;

namespace ProtectWebsite.Services.Tracking;

public static class RequestContextAccessor
{
    public static ClientContextResolution Resolve(HttpContext httpContext)
    {
        var userAgent = httpContext?.Request?.Headers?.UserAgent.ToString();
        var acceptLanguage = httpContext?.Request?.Headers?.AcceptLanguage.ToString();

        var parsed = ClientContextResolver.ParseUserAgent(userAgent);

        return ClientContextResolver.Resolve(
            deviceType: parsed.DeviceType,
            browser: parsed.Browser,
            operatingSystem: parsed.OperatingSystem,
            userAgent: userAgent,
            viewportWidth: null,
            viewportHeight: null,
            screenWidth: null,
            screenHeight: null,
            webDriver: null,
            isHeadless: null,
            mouseMoveCount: null,
            humanInteractionCount: null,
            visibilityChangeCount: null,
            language: ClientContextResolver.NormalizeAcceptLanguage(acceptLanguage),
            timeZone: null
        );
    }
}
