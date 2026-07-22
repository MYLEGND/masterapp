namespace ClientApp.Services;

/// <summary>
/// Chooses the single ClientApp sign-in entry URL for unauthenticated requests.
/// </summary>
public sealed class ClientAppSignInEntryPoint
{
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public ClientAppSignInEntryPoint(ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    public string Resolve(HttpContext httpContext)
    {
        var target = _returnUrlNormalizer.Normalize($"{httpContext.Request.Path}{httpContext.Request.QueryString}");
        return $"/Account/Login?returnUrl={Uri.EscapeDataString(target)}";
    }
}
