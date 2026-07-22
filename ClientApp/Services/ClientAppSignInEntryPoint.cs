namespace ClientApp.Services;

/// <summary>
/// Chooses the single ClientApp sign-in entry URL for unauthenticated requests.
/// </summary>
public sealed class ClientAppSignInEntryPoint
{
    private readonly ClientIdentityAccessService _identityAccessService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;

    public ClientAppSignInEntryPoint(
        ClientIdentityAccessService identityAccessService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _identityAccessService = identityAccessService;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    public async Task<string> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var target = _returnUrlNormalizer.Normalize($"{httpContext.Request.Path}{httpContext.Request.QueryString}");
        var hasContinuation = await _identityAccessService.HasValidChallengeContinuationAsync(httpContext, cancellationToken);
        var entryPath = hasContinuation ? "/Account/AzureLogin" : "/Account/Login";
        return $"{entryPath}?returnUrl={Uri.EscapeDataString(target)}";
    }
}
