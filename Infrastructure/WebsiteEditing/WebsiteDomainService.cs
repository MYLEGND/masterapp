using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

/// <summary>Cloudflare for SaaS is the sole customer-domain and certificate authority.</summary>
public sealed class WebsiteDomainService(MasterAppDbContext db, IHttpClientFactory clients, IConfiguration configuration)
{
    public static string NormalizeHostname(string hostname)
    {
        var value = new IdnMapping().GetAscii((hostname ?? "").Trim().TrimEnd('.')).ToLowerInvariant();
        if (value.Length > 253 || !value.Contains('.') || Uri.CheckHostName(value) != UriHostNameType.Dns ||
            value.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')) ||
            value == "mylegnd.com" || value.EndsWith(".mylegnd.com", StringComparison.Ordinal))
            throw new ArgumentException("Enter a business-owned domain, without a protocol or path.");
        return value;
    }

    public async Task<WebsiteDomainBinding> RegisterAsync(Guid businessId, string hostname, CancellationToken ct = default)
    {
        hostname = NormalizeHostname(hostname);
        if (!await db.CommerceBusinesses.AnyAsync(b => b.Id == businessId && b.IsActive && b.Status == "Active", ct))
            throw new InvalidOperationException("Business is unavailable.");
        var binding = await db.Set<WebsiteDomainBinding>().SingleOrDefaultAsync(x => x.Hostname == hostname, ct);
        if (binding is not null)
        {
            if (binding.CommerceBusinessId != businessId) throw new InvalidOperationException("Domain is already assigned.");
            return await RefreshAsync(businessId, binding.Id, ct);
        }
        // Reserve before the external call: unique hostname prevents cross-business races.
        EnsureConfigured();
        binding = new WebsiteDomainBinding { CommerceBusinessId = businessId, Hostname = hostname };
        db.Add(binding);
        await db.SaveChangesAsync(ct);
        return await RefreshAsync(businessId, binding.Id, ct);
    }

    public async Task<WebsiteDomainBinding> RefreshAsync(Guid businessId, Guid bindingId, CancellationToken ct = default)
    {
        var binding = await db.Set<WebsiteDomainBinding>().SingleAsync(x => x.Id == bindingId && x.CommerceBusinessId == businessId, ct);
        EnsureConfigured();
        JsonElement result;
        if (string.IsNullOrEmpty(binding.ProviderHostnameId))
        {
            // Recover only legacy provider receipts that carry the exact LEGEND ownership metadata.
            // New hostnames keep tenant ownership exclusively in WebsiteDomainBinding so Cloudflare
            // Custom Metadata is not a runtime dependency.
            var existing = await CallAsync(HttpMethod.Get, "?hostname=" + Uri.EscapeDataString(binding.Hostname), null, ct);
            var matches = existing.EnumerateArray().Where(x => x.GetProperty("hostname").GetString() == binding.Hostname).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous domain provider receipt.");
            if (matches.Length == 1)
            {
                if (!HasMatchingLegendMetadata(binding, matches[0]))
                    throw new InvalidOperationException("Domain provider hostname exists without a trusted LEGEND receipt.");
                result = matches[0];
            }
            else
            {
                result = await CallAsync(HttpMethod.Post, "", new
                {
                    hostname = binding.Hostname,
                    ssl = new { method = "http", type = "dv" }
                }, ct);
            }
        }
        else result = await CallAsync(HttpMethod.Get, "/" + Uri.EscapeDataString(binding.ProviderHostnameId), null, ct);

        // The editor promises a one-record onboarding flow: the customer points the
        // hostname at the LEGEND SaaS CNAME target and Cloudflare completes DCV.
        // Migrate older TXT-created pending hostnames and explicitly retrigger HTTP
        // DCV on user/background refresh so an already-correct CNAME can advance.
        if (ShouldRetryAutomaticHttpDcv(result))
        {
            var providerId = result.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Missing domain receipt.");
            result = await CallAsync(HttpMethod.Patch, "/" + Uri.EscapeDataString(providerId), new
            {
                ssl = new { method = "http", type = "dv" }
            }, ct);
        }

        ApplyReceipt(binding, result);
        var routing = string.Equals(binding.CertificateStatus, "active", StringComparison.OrdinalIgnoreCase)
            ? await ProbeRoutingAsync(binding, ct)
            : new WebsiteDomainRoutingProof(false, "routing_not_checked", "LEGEND routing proof has not run because HTTPS is not active yet.", null);
        if (binding.Status == "active" && !routing.Confirmed) binding.Status = "pending";
        ApplyVerificationDiagnostic(binding, result, routing, CnameTarget);
        binding.LastCheckedUtc = DateTime.UtcNow;
        binding.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return binding;
    }

    public async Task RemoveAsync(Guid businessId, Guid bindingId, CancellationToken ct = default)
    {
        var binding = await db.Set<WebsiteDomainBinding>().SingleAsync(x => x.Id == bindingId && x.CommerceBusinessId == businessId, ct);
        // Disable routing before provider removal; errors retain retryable local state.
        binding.Status = "removing";
        binding.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        if (binding.ProviderHostnameId.Length > 0)
            await CallAsync(HttpMethod.Delete, "/" + Uri.EscapeDataString(binding.ProviderHostnameId), null, ct);
        db.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid?> ResolveAsync(string hostname, CancellationToken ct = default)
    {
        try { hostname = NormalizeHostname(hostname); } catch (ArgumentException) { return null; }
        var cutoff = DateTime.UtcNow.AddHours(-24);
        return await (from binding in db.Set<WebsiteDomainBinding>().AsNoTracking()
                      join business in db.CommerceBusinesses.AsNoTracking() on binding.CommerceBusinessId equals business.Id
                      where binding.Hostname == hostname && binding.Status == "active" && binding.CertificateStatus == "active" &&
                            binding.LastCheckedUtc >= cutoff && business.IsActive && business.Status == "Active"
                      select (Guid?)business.Id).SingleOrDefaultAsync(ct);
    }

    public static void ApplyReceipt(WebsiteDomainBinding binding, JsonElement result)
    {
        if (result.GetProperty("hostname").GetString() != binding.Hostname)
            throw new InvalidOperationException("Domain provider returned a different hostname.");
        ValidateOptionalLegendMetadata(binding, result);
        binding.ProviderHostnameId = result.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing domain receipt.");
        binding.CertificateStatus = result.GetProperty("ssl").GetProperty("status").GetString() ?? "pending";
        var status = result.GetProperty("status").GetString() ?? "pending";
        binding.Status = status == "active" && binding.CertificateStatus == "active" ? "active" : "pending";
        var ssl = result.GetProperty("ssl");
        binding.VerificationJson = JsonSerializer.Serialize(new
        {
            providerStatus = status,
            certificateStatus = binding.CertificateStatus,
            validationMethod = ssl.TryGetProperty("method", out var method) ? method.GetString() : null,
            verificationErrors = result.TryGetProperty("verification_errors", out var verificationErrors) ? verificationErrors : (JsonElement?)null,
            certificateErrors = ssl.TryGetProperty("validation_errors", out var certificateErrors) ? certificateErrors : (JsonElement?)null,
            ownership = result.TryGetProperty("ownership_verification", out var ownership) ? ownership : (JsonElement?)null,
            certificate = ssl.TryGetProperty("validation_records", out var records) ? records : (JsonElement?)null
        });
    }

    private static bool HasMatchingLegendMetadata(WebsiteDomainBinding binding, JsonElement result)
    {
        if (!result.TryGetProperty("custom_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return false;
        return metadata.TryGetProperty("legend_binding_id", out var bindingId) &&
            bindingId.GetString() == binding.Id.ToString("N") &&
            metadata.TryGetProperty("legend_business_id", out var businessId) &&
            businessId.GetString() == binding.CommerceBusinessId.ToString("N");
    }

    private static void ValidateOptionalLegendMetadata(WebsiteDomainBinding binding, JsonElement result)
    {
        if (!result.TryGetProperty("custom_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return;
        var hasBinding = metadata.TryGetProperty("legend_binding_id", out var bindingId);
        var hasBusiness = metadata.TryGetProperty("legend_business_id", out var businessId);
        if (!hasBinding && !hasBusiness) return;
        if (!hasBinding || !hasBusiness ||
            bindingId.GetString() != binding.Id.ToString("N") ||
            businessId.GetString() != binding.CommerceBusinessId.ToString("N"))
            throw new InvalidOperationException("Domain provider ownership does not match this business.");
    }

    private static bool ShouldRetryAutomaticHttpDcv(JsonElement result)
    {
        var status = result.TryGetProperty("status", out var providerStatus) ? providerStatus.GetString() : null;
        if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!result.TryGetProperty("ssl", out var ssl)) return false;
        var certificateStatus = ssl.TryGetProperty("status", out var sslStatus) ? sslStatus.GetString() : null;
        return !string.Equals(certificateStatus, "active", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record WebsiteDomainRoutingProof(
        bool Confirmed,
        string Code,
        string Message,
        int? HttpStatus);

    private static async Task<WebsiteDomainRoutingProof> ProbeRoutingAsync(WebsiteDomainBinding binding, CancellationToken ct)
    {
        using var handler = LegendConnectResearchNetworkPolicy.CreatePublicReadOnlyHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.GetAsync("https://" + binding.Hostname + "/.well-known/legend-website", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new WebsiteDomainRoutingProof(
                    false,
                    "routing_http_status",
                    $"LEGEND routing proof reached the hostname, but /.well-known/legend-website returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4097];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }

            if (length > 4096)
                return new WebsiteDomainRoutingProof(false, "routing_response_too_large", "LEGEND routing proof returned an oversized response instead of the expected binding receipt.", (int)response.StatusCode);

            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (!json.RootElement.TryGetProperty("businessId", out var business) || !business.TryGetGuid(out var businessId))
                return new WebsiteDomainRoutingProof(false, "routing_business_missing", "LEGEND routing proof response is missing a valid businessId.", (int)response.StatusCode);
            if (businessId != binding.CommerceBusinessId)
                return new WebsiteDomainRoutingProof(false, "routing_business_mismatch", "The hostname is reaching LEGEND, but it resolves to a different business.", (int)response.StatusCode);
            if (!json.RootElement.TryGetProperty("bindingId", out var value) || !value.TryGetGuid(out var bindingId))
                return new WebsiteDomainRoutingProof(false, "routing_binding_missing", "LEGEND routing proof response is missing a valid bindingId.", (int)response.StatusCode);
            if (bindingId != binding.Id)
                return new WebsiteDomainRoutingProof(false, "routing_binding_mismatch", "The hostname is reaching LEGEND, but it resolves to a different domain binding.", (int)response.StatusCode);

            return new WebsiteDomainRoutingProof(true, "routing_confirmed", "LEGEND routing proof matched this business and domain binding.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebsiteDomainRoutingProof(false, "routing_timeout", "LEGEND could not complete the HTTPS routing proof within 10 seconds.", null);
        }
        catch (HttpRequestException)
        {
            return new WebsiteDomainRoutingProof(false, "routing_unreachable", "LEGEND could not reach this hostname over public HTTPS.", null);
        }
        catch (JsonException)
        {
            return new WebsiteDomainRoutingProof(false, "routing_invalid_json", "The hostname responded, but /.well-known/legend-website did not return a valid LEGEND routing receipt.", null);
        }
    }

    internal static void ApplyVerificationDiagnostic(
        WebsiteDomainBinding binding,
        JsonElement result,
        WebsiteDomainRoutingProof routing,
        string cnameTarget)
    {
        var providerStatus = result.TryGetProperty("status", out var provider) ? provider.GetString() ?? "pending" : "pending";
        var ssl = result.TryGetProperty("ssl", out var sslValue) ? sslValue : default;
        var certificateStatus = ssl.ValueKind == JsonValueKind.Object && ssl.TryGetProperty("status", out var sslStatus)
            ? sslStatus.GetString() ?? binding.CertificateStatus
            : binding.CertificateStatus;
        var providerErrors = ErrorMessages(result, "verification_errors");
        var certificateErrors = ssl.ValueKind == JsonValueKind.Object ? ErrorMessages(ssl, "validation_errors") : Array.Empty<string>();

        var summary = BuildDiagnosticSummary(providerStatus, certificateStatus, providerErrors, certificateErrors, routing);
        var requiredAction = BuildRequiredAction(providerStatus, certificateStatus, routing, cnameTarget);

        binding.VerificationJson = JsonSerializer.Serialize(new
        {
            providerStatus,
            certificateStatus,
            validationMethod = ssl.ValueKind == JsonValueKind.Object && ssl.TryGetProperty("method", out var method) ? method.GetString() : null,
            providerErrors,
            certificateErrors,
            routing = new
            {
                status = routing.Confirmed ? "confirmed" : "failed",
                code = routing.Code,
                message = routing.Message,
                httpStatus = routing.HttpStatus
            },
            summary,
            requiredAction
        });
    }

    private static string[] ErrorMessages(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var errors) || errors.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return errors.EnumerateArray()
            .Select(error =>
                error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                        ? message.GetString()
                        : error.GetRawText())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static string BuildDiagnosticSummary(
        string providerStatus,
        string certificateStatus,
        IReadOnlyList<string> providerErrors,
        IReadOnlyList<string> certificateErrors,
        WebsiteDomainRoutingProof routing)
    {
        if (!string.Equals(providerStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            var detail = providerErrors.FirstOrDefault();
            return detail is null
                ? $"Cloudflare hostname status is {providerStatus}; HTTPS is {certificateStatus}; {routing.Message}"
                : $"Cloudflare hostname status is {providerStatus}: {detail} HTTPS is {certificateStatus}; {routing.Message}";
        }

        if (!string.Equals(certificateStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            var detail = certificateErrors.FirstOrDefault();
            return detail is null
                ? $"Cloudflare hostname is active, but HTTPS is {certificateStatus}."
                : $"Cloudflare hostname is active, but HTTPS is {certificateStatus}: {detail}";
        }

        return routing.Confirmed
            ? "Cloudflare hostname, HTTPS, and LEGEND routing proof are all active."
            : $"Cloudflare hostname and HTTPS are active, but {routing.Message}";
    }

    private static string BuildRequiredAction(
        string providerStatus,
        string certificateStatus,
        WebsiteDomainRoutingProof routing,
        string cnameTarget)
    {
        if (!string.Equals(providerStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (routing.Confirmed)
                return "No LEGEND routing change is required. Cloudflare still reports the custom hostname as pending; choose Verify status again after Cloudflare advances the hostname to active.";

            return routing.Code switch
            {
                "routing_http_status" or "routing_invalid_json" or "routing_business_missing" or "routing_business_mismatch" or "routing_binding_missing" or "routing_binding_mismatch" or "routing_response_too_large" =>
                    $"The hostname is reaching a server, but not the expected LEGEND business binding. Ensure the website routing record for this hostname points only to {cnameTarget} and remove conflicting website routing/forwarding records, then choose Verify status.",
                "routing_timeout" or "routing_unreachable" =>
                    $"LEGEND cannot reach the hostname over public HTTPS. Confirm the website routing record points to {cnameTarget}, remove conflicting A/AAAA/CNAME or forwarding records for this hostname, then choose Verify status.",
                _ =>
                    $"Confirm the website routing record points to {cnameTarget}, then choose Verify status."
            };
        }

        if (!string.Equals(certificateStatus, "active", StringComparison.OrdinalIgnoreCase))
            return "The hostname is active but Cloudflare has not finished HTTPS validation. Follow the certificate validation error shown above, then choose Verify status.";

        return routing.Confirmed
            ? "No action is required."
            : routing.Code switch
            {
                "routing_http_status" or "routing_invalid_json" or "routing_business_missing" or "routing_business_mismatch" or "routing_binding_missing" or "routing_binding_mismatch" or "routing_response_too_large" =>
                    $"Cloudflare is active, but the hostname is not reaching this LEGEND business binding. Ensure the website routing record points only to {cnameTarget} and remove conflicting website routing/forwarding records, then choose Verify status.",
                "routing_timeout" or "routing_unreachable" =>
                    $"Cloudflare is active, but LEGEND cannot reach the hostname over public HTTPS. Confirm the website routing record points to {cnameTarget}, remove conflicting A/AAAA/CNAME or forwarding records, then choose Verify status.",
                _ => "Choose Verify status again. If the routing proof still fails, use the routing diagnostic shown above."
            };
    }

    public string CnameTarget => configuration["WebsiteDomains:CnameTarget"] ?? "";
    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(configuration["WebsiteDomains:CloudflareZoneId"]) ||
            string.IsNullOrWhiteSpace(configuration["WebsiteDomains:ApiToken"]) || string.IsNullOrWhiteSpace(CnameTarget))
            throw new InvalidOperationException("Customer domain hosting is not configured.");
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, string suffix, object? body, CancellationToken ct)
    {
        EnsureConfigured();
        var zone = Uri.EscapeDataString(configuration["WebsiteDomains:CloudflareZoneId"]!);
        using var request = new HttpRequestMessage(method, "https://api.cloudflare.com/client/v4/zones/" + zone + "/custom_hostnames" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["WebsiteDomains:ApiToken"]);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await clients.CreateClient("WebsiteDomains").SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Domain provider did not confirm the operation.");
        return json.RootElement.GetProperty("result").Clone();
    }
}
