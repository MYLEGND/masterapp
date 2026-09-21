using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Infrastructure.WebsiteEditing;

/// <summary>Extends the existing editor policy with verified customer origins.</summary>
public sealed class WebsiteCorsPolicyProvider(IOptions<CorsOptions> options, WebsiteDomainService domains) : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var policy = options.Value.GetPolicy(policyName ?? options.Value.DefaultPolicyName);
        if (policyName != "PublicWebsiteEditor" || policy is null) return policy;
        var origin = context.Request.Headers.Origin.ToString();
        if (policy.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return policy;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.AbsolutePath != "/") return policy;
        if (await domains.ResolveAsync(uri.Host, context.RequestAborted) is null) return policy;
        return new CorsPolicyBuilder().WithOrigins(origin).AllowAnyHeader().WithMethods("GET", "POST").Build();
    }
}
