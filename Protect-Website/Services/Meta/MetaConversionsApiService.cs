using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ProtectWebsite.Services.Meta;

public interface IMetaConversionsApiService
{
    Task<MetaConversionsApiResult> SendLeadAsync(MetaLeadConversionRequest request, CancellationToken cancellationToken = default);
}

public sealed class MetaOptions
{
    public string? PixelId { get; set; }
    public string? AccessToken { get; set; }
    public string? TestEventCode { get; set; }
}

public sealed class MetaLeadConversionRequest
{
    public Guid LeadId { get; init; }
    public Guid CorrelationId { get; init; }
    public string EventId { get; init; } = string.Empty;
    public string QuoteType { get; init; } = string.Empty;
    public string PageKey { get; init; } = string.Empty;
    public string OfferKey { get; init; } = string.Empty;
    public string? EventSourceUrl { get; init; }
    public string? ClientIpAddress { get; init; }
    public string? ClientUserAgent { get; init; }
    public string? Fbp { get; init; }
    public string? Fbc { get; init; }
    public string? Fbclid { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public bool AllowHashedContactData { get; init; }
    public DateTime EventUtc { get; init; }
}

public sealed class MetaConversionsApiResult
{
    public bool Attempted { get; init; }
    public bool Sent { get; init; }
    public string Status { get; init; } = "unknown";
    public string? Note { get; init; }
}

public sealed class MetaConversionsApiService : IMetaConversionsApiService
{
    private const string ApiVersion = "v21.0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<MetaOptions> _options;
    private readonly ILogger<MetaConversionsApiService> _logger;

    public MetaConversionsApiService(
        HttpClient httpClient,
        IOptions<MetaOptions> options,
        ILogger<MetaConversionsApiService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<MetaConversionsApiResult> SendLeadAsync(MetaLeadConversionRequest request, CancellationToken cancellationToken = default)
    {
        var pixelId = _options.Value.PixelId?.Trim();
        var accessToken = _options.Value.AccessToken?.Trim();
        var testEventCode = _options.Value.TestEventCode?.Trim();

        if (string.IsNullOrWhiteSpace(pixelId) || string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogInformation(
                "MetaCapi [{CorrelationId}]: skipped lead {LeadId} quoteType={QuoteType} eventId={EventId} status={Status}",
                request.CorrelationId, request.LeadId, request.QuoteType, request.EventId, "skipped_not_configured");

            return new MetaConversionsApiResult
            {
                Attempted = false,
                Sent = false,
                Status = "skipped_not_configured",
                Note = "meta_config_missing"
            };
        }

        var endpoint = $"https://graph.facebook.com/{ApiVersion}/{pixelId}/events";
        var userData = BuildUserData(request);
        var eventPayload = new Dictionary<string, object?>
        {
            ["event_name"] = "Lead",
            ["event_time"] = new DateTimeOffset(request.EventUtc).ToUnixTimeSeconds(),
            ["event_id"] = request.EventId,
            ["action_source"] = "website",
            ["event_source_url"] = request.EventSourceUrl,
            ["user_data"] = userData,
            ["custom_data"] = BuildCustomData(request)
        };

        var formFields = new Dictionary<string, string>
        {
            ["access_token"] = accessToken,
            ["data"] = JsonSerializer.Serialize(new[] { eventPayload }, JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(testEventCode))
            formFields["test_event_code"] = testEventCode;

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(formFields)
            };

            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "MetaCapi [{CorrelationId}]: sent lead {LeadId} quoteType={QuoteType} eventId={EventId} status={Status}",
                    request.CorrelationId, request.LeadId, request.QuoteType, request.EventId, "sent");

                return new MetaConversionsApiResult
                {
                    Attempted = true,
                    Sent = true,
                    Status = "sent"
                };
            }

            var safeNote = BuildSafeErrorNote(responseBody, response.ReasonPhrase);
            _logger.LogWarning(
                "MetaCapi [{CorrelationId}]: failed lead {LeadId} quoteType={QuoteType} eventId={EventId} status={Status} note={Note}",
                request.CorrelationId, request.LeadId, request.QuoteType, request.EventId, "failed", safeNote);

            return new MetaConversionsApiResult
            {
                Attempted = true,
                Sent = false,
                Status = "failed",
                Note = safeNote
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MetaCapi [{CorrelationId}]: exception lead {LeadId} quoteType={QuoteType} eventId={EventId} status={Status}",
                request.CorrelationId, request.LeadId, request.QuoteType, request.EventId, "failed_exception");

            return new MetaConversionsApiResult
            {
                Attempted = true,
                Sent = false,
                Status = "failed",
                Note = "exception"
            };
        }
    }

    private static Dictionary<string, object?> BuildUserData(MetaLeadConversionRequest request)
    {
        var userData = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.ClientIpAddress))
            userData["client_ip_address"] = request.ClientIpAddress;

        if (!string.IsNullOrWhiteSpace(request.ClientUserAgent))
            userData["client_user_agent"] = request.ClientUserAgent;

        if (!string.IsNullOrWhiteSpace(request.Fbp))
            userData["fbp"] = request.Fbp;

        var fbc = ResolveFbc(request.Fbc, request.Fbclid, request.EventUtc);
        if (!string.IsNullOrWhiteSpace(fbc))
            userData["fbc"] = fbc;

        if (request.AllowHashedContactData)
        {
            var emailHash = HashSha256(NormalizeEmail(request.Email));
            if (!string.IsNullOrWhiteSpace(emailHash))
                userData["em"] = new[] { emailHash };

            var phoneHash = HashSha256(NormalizePhone(request.Phone));
            if (!string.IsNullOrWhiteSpace(phoneHash))
                userData["ph"] = new[] { phoneHash };
        }

        return userData;
    }

    private static Dictionary<string, object?> BuildCustomData(MetaLeadConversionRequest request)
    {
        var customData = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.PageKey))
            customData["content_name"] = request.PageKey;

        if (!string.IsNullOrWhiteSpace(request.OfferKey))
            customData["content_category"] = request.OfferKey;

        return customData;
    }

    private static string? ResolveFbc(string? fbc, string? fbclid, DateTime eventUtc)
    {
        if (!string.IsNullOrWhiteSpace(fbc))
            return fbc.Trim();

        if (string.IsNullOrWhiteSpace(fbclid))
            return null;

        return $"fb.1.{new DateTimeOffset(eventUtc).ToUnixTimeMilliseconds()}.{fbclid.Trim()}";
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var normalized = email.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string? HashSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildSafeErrorNote(string? responseBody, string? reasonPhrase)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return string.IsNullOrWhiteSpace(reasonPhrase) ? "http_error" : $"http_error:{reasonPhrase.Trim()}";

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return string.IsNullOrWhiteSpace(reasonPhrase) ? "http_error" : $"http_error:{reasonPhrase.Trim()}";

            var type = error.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;
            var code = error.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var codeValue)
                ? codeValue.ToString()
                : null;
            var subcode = error.TryGetProperty("error_subcode", out var subcodeEl) && subcodeEl.TryGetInt32(out var subcodeValue)
                ? subcodeValue.ToString()
                : null;

            var parts = new[] { type, code, subcode }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            return parts.Length == 0 ? "meta_error" : string.Join(":", parts);
        }
        catch
        {
            return string.IsNullOrWhiteSpace(reasonPhrase) ? "http_error" : $"http_error:{reasonPhrase.Trim()}";
        }
    }
}
