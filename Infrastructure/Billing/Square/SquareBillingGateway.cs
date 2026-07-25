using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Billing;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Billing.Square;

internal sealed class SquareBillingGateway : IBillingGateway
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SquareBillingOptions _options;
    private readonly ILogger<SquareBillingGateway> _logger;

    public SquareBillingGateway(
        IHttpClientFactory httpClientFactory,
        SquareBillingOptions options,
        ILogger<SquareBillingGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public BillingProvider Provider => BillingProvider.Square;
    public BillingProviderEnvironment Environment => _options.Environment;

    public async Task<BillingOneTimePaymentResult> CreateOneTimePaymentAsync(BillingOneTimePaymentRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("ONE_TIME_PAYMENT");
        if (configurationFailure is not null)
            return configurationFailure;

        var body = new Dictionary<string, object?>
        {
            ["idempotency_key"] = ToSquareIdempotencyKey(request.IdempotencyKey),
            ["source_id"] = request.SourceId,
            ["location_id"] = _options.LocationId,
            ["amount_money"] = new Dictionary<string, object?>
            {
                ["amount"] = request.AmountCents,
                ["currency"] = request.Currency
            },
            ["autocomplete"] = true,
            ["note"] = request.Note
        };

        if (!string.IsNullOrWhiteSpace(request.ExistingProviderCustomerId))
            body["customer_id"] = request.ExistingProviderCustomerId;

        if (!string.IsNullOrWhiteSpace(request.OrderReference))
            body["reference_id"] = request.OrderReference;

        var response = await SendAsync(HttpMethod.Post, "/v2/payments", body, request.CorrelationId, cancellationToken);
        if (!response.Success)
            return new BillingOneTimePaymentResult(false, null, response.NormalizedStatus, response.SafeErrorCode, response.SanitizedSummary, response.ProviderRequestId, response.Retryable);

        var payment = response.Json?.RootElement.TryGetProperty("payment", out var paymentElement) == true ? paymentElement : default;
        return new BillingOneTimePaymentResult(
            true,
            GetString(payment, "id"),
            GetString(payment, "status") ?? response.NormalizedStatus,
            null,
            "Square one-time payment created.",
            response.ProviderRequestId,
            false,
            GetInt(payment, "amount_money", "amount"),
            GetString(payment, "amount_money", "currency"),
            ParseDateTime(GetString(payment, "created_at")));
    }

    public async Task<BillingCustomerResolutionResult> ResolveCustomerAsync(BillingCustomerResolutionRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("CUSTOMER_RESOLUTION");
        if (configurationFailure is not null)
            return new BillingCustomerResolutionResult(false, null, configurationFailure.NormalizedStatus, configurationFailure.SafeErrorCode, configurationFailure.SanitizedSummary, configurationFailure.ProviderRequestId, configurationFailure.Retryable);

        if (!string.IsNullOrWhiteSpace(request.ExistingProviderCustomerId))
        {
            var existing = await SendAsync(HttpMethod.Get, $"/v2/customers/{Uri.EscapeDataString(request.ExistingProviderCustomerId)}", null, request.CorrelationId, cancellationToken);
            if (existing.Success)
            {
                var customer = existing.Json?.RootElement.TryGetProperty("customer", out var customerElement) == true ? customerElement : default;
                var customerId = GetString(customer, "id") ?? request.ExistingProviderCustomerId;
                return new BillingCustomerResolutionResult(true, customerId, "RESOLVED", null, "Square customer resolved from existing provider ID.", existing.ProviderRequestId, false, customerId);
            }
        }

        var exactEmail = request.Customer.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(exactEmail))
        {
            var searchBody = new Dictionary<string, object?>
            {
                ["limit"] = 1,
                ["query"] = new Dictionary<string, object?>
                {
                    ["filter"] = new Dictionary<string, object?>
                    {
                        ["email_address"] = new Dictionary<string, object?>
                        {
                            ["exact"] = exactEmail
                        }
                    }
                }
            };

            var search = await SendAsync(HttpMethod.Post, "/v2/customers/search", searchBody, request.CorrelationId, cancellationToken);
            if (search.Success && search.Json?.RootElement.TryGetProperty("customers", out var customers) == true && customers.ValueKind == JsonValueKind.Array && customers.GetArrayLength() > 0)
            {
                var matchedCustomer = customers[0];
                var matchedCustomerId = GetString(matchedCustomer, "id");
                return new BillingCustomerResolutionResult(true, matchedCustomerId, "RESOLVED", null, "Square customer resolved from email search.", search.ProviderRequestId, false, matchedCustomerId);
            }
        }

        var createBody = new Dictionary<string, object?>
        {
            ["idempotency_key"] = ToSquareIdempotencyKey(
                request.IdempotencyKey ?? BillingIdempotency.CreateDeterministic("square-customer", request.Customer.Email, request.Customer.ReferenceId)),
            ["given_name"] = request.Customer.GivenName,
            ["family_name"] = request.Customer.FamilyName,
            ["email_address"] = request.Customer.Email,
            ["phone_number"] = request.Customer.Phone,
            ["reference_id"] = request.Customer.ReferenceId,
            ["note"] = request.Customer.Note
        };

        var customerAddress = BuildAddress(request.Customer.Address);
        if (customerAddress is not null)
            createBody["address"] = customerAddress;

        var created = await SendAsync(HttpMethod.Post, "/v2/customers", createBody, request.CorrelationId, cancellationToken);
        if (!created.Success)
            return new BillingCustomerResolutionResult(false, null, created.NormalizedStatus, created.SafeErrorCode, created.SanitizedSummary, created.ProviderRequestId, created.Retryable);

        var createdCustomer = created.Json?.RootElement.TryGetProperty("customer", out var createdCustomerElement) == true ? createdCustomerElement : default;
        var providerCustomerId = GetString(createdCustomer, "id");
        return new BillingCustomerResolutionResult(true, providerCustomerId, "CREATED", null, "Square customer created.", created.ProviderRequestId, false, providerCustomerId);
    }

    public async Task<BillingPaymentMethodAttachmentResult> AttachPaymentMethodAsync(BillingPaymentMethodAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("ATTACH_PAYMENT_METHOD");
        if (configurationFailure is not null)
            return new BillingPaymentMethodAttachmentResult(false, null, configurationFailure.NormalizedStatus, configurationFailure.SafeErrorCode, configurationFailure.SanitizedSummary, configurationFailure.ProviderRequestId, configurationFailure.Retryable);

        var body = new Dictionary<string, object?>
        {
            ["idempotency_key"] = ToSquareIdempotencyKey(request.IdempotencyKey),
            ["source_id"] = request.SourceId,
            ["card"] = new Dictionary<string, object?>
            {
                ["customer_id"] = request.ProviderCustomerId,
                ["cardholder_name"] = request.CardholderName,
                ["reference_id"] = request.ReferenceId
            }
        };

        if (body["card"] is Dictionary<string, object?> cardBody)
        {
            var billingAddress = BuildAddress(request.BillingAddress);
            if (billingAddress is not null)
                cardBody["billing_address"] = billingAddress;
        }

        if (!string.IsNullOrWhiteSpace(request.VerificationToken))
            body["verification_token"] = request.VerificationToken;

        var response = await SendAsync(HttpMethod.Post, "/v2/cards", body, request.CorrelationId, cancellationToken);
        if (!response.Success)
            return new BillingPaymentMethodAttachmentResult(false, null, response.NormalizedStatus, response.SafeErrorCode, response.SanitizedSummary, response.ProviderRequestId, response.Retryable, request.ProviderCustomerId);

        var card = response.Json?.RootElement.TryGetProperty("card", out var cardElement) == true ? cardElement : default;
        var cardId = GetString(card, "id");
        return new BillingPaymentMethodAttachmentResult(
            true,
            cardId,
            GetString(card, "card_status") ?? "ATTACHED",
            null,
            "Square payment method attached.",
            response.ProviderRequestId,
            false,
            request.ProviderCustomerId,
            cardId,
            GetString(card, "card_brand"),
            GetString(card, "last_4"),
            GetInt(card, "exp_month"),
            GetInt(card, "exp_year"),
            GetString(card, "cardholder_name"));
    }

    public async Task<BillingPaymentMethodDisableResult> DisablePaymentMethodAsync(BillingPaymentMethodDisableRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("DISABLE_PAYMENT_METHOD");
        if (configurationFailure is not null)
        {
            return new BillingPaymentMethodDisableResult(
                false,
                null,
                configurationFailure.NormalizedStatus,
                configurationFailure.SafeErrorCode,
                configurationFailure.SanitizedSummary,
                configurationFailure.ProviderRequestId,
                configurationFailure.Retryable);
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentMethodId))
        {
            return new BillingPaymentMethodDisableResult(
                false,
                null,
                "VALIDATION_ERROR",
                "PAYMENT_METHOD_ID_REQUIRED",
                "The payment method could not be identified.",
                null,
                false);
        }

        var response = await SendAsync(
            HttpMethod.Post,
            $"/v2/cards/{Uri.EscapeDataString(request.ProviderPaymentMethodId)}/disable",
            new Dictionary<string, object?>(),
            request.CorrelationId,
            cancellationToken);
        if (!response.Success)
        {
            return new BillingPaymentMethodDisableResult(
                false,
                request.ProviderPaymentMethodId,
                response.NormalizedStatus,
                response.SafeErrorCode,
                response.SanitizedSummary,
                response.ProviderRequestId,
                response.Retryable);
        }

        var card = response.Json?.RootElement.TryGetProperty("card", out var cardElement) == true ? cardElement : default;
        return new BillingPaymentMethodDisableResult(
            true,
            GetString(card, "id") ?? request.ProviderPaymentMethodId,
            GetString(card, "card_status") ?? "DISABLED",
            null,
            "Payment method disabled.",
            response.ProviderRequestId,
            false);
    }

    public async Task<BillingPaymentResult> GetPaymentAsync(BillingPaymentLookupRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("GET_PAYMENT");
        if (configurationFailure is not null)
            return new BillingPaymentResult(false, request.ProviderPaymentId, configurationFailure.NormalizedStatus, configurationFailure.SafeErrorCode, configurationFailure.SanitizedSummary, configurationFailure.ProviderRequestId, configurationFailure.Retryable);

        var response = await SendAsync(HttpMethod.Get, $"/v2/payments/{Uri.EscapeDataString(request.ProviderPaymentId)}", null, request.CorrelationId, cancellationToken);
        if (!response.Success)
            return new BillingPaymentResult(false, request.ProviderPaymentId, response.NormalizedStatus, response.SafeErrorCode, response.SanitizedSummary, response.ProviderRequestId, response.Retryable);

        var payment = response.Json?.RootElement.TryGetProperty("payment", out var paymentElement) == true ? paymentElement : default;
        return new BillingPaymentResult(
            true,
            GetString(payment, "id"),
            GetString(payment, "status") ?? response.NormalizedStatus,
            null,
            "Square payment retrieved.",
            response.ProviderRequestId,
            false,
            GetString(payment, "invoice_id"),
            null,
            GetInt(payment, "amount_money", "amount"),
            GetString(payment, "amount_money", "currency"),
            ParseDateTime(GetString(payment, "updated_at") ?? GetString(payment, "created_at")));
    }

    public async Task<BillingPaymentResult> GetRefundAsync(BillingRefundLookupRequest request, CancellationToken cancellationToken = default)
    {
        var configurationFailure = ValidateConfigured("GET_REFUND");
        if (configurationFailure is not null)
            return new BillingPaymentResult(false, null, configurationFailure.NormalizedStatus, configurationFailure.SafeErrorCode, configurationFailure.SanitizedSummary, configurationFailure.ProviderRequestId, configurationFailure.Retryable, null, request.ProviderRefundId);

        var response = await SendAsync(HttpMethod.Get, $"/v2/refunds/{Uri.EscapeDataString(request.ProviderRefundId)}", null, request.CorrelationId, cancellationToken);
        if (!response.Success)
            return new BillingPaymentResult(false, null, response.NormalizedStatus, response.SafeErrorCode, response.SanitizedSummary, response.ProviderRequestId, response.Retryable, null, request.ProviderRefundId);

        var refund = response.Json?.RootElement.TryGetProperty("refund", out var refundElement) == true ? refundElement : default;
        return new BillingPaymentResult(
            true,
            GetString(refund, "payment_id"),
            GetString(refund, "status") ?? response.NormalizedStatus,
            null,
            "Square refund retrieved.",
            response.ProviderRequestId,
            false,
            GetString(refund, "invoice_id"),
            GetString(refund, "id"),
            GetInt(refund, "amount_money", "amount"),
            GetString(refund, "amount_money", "currency"),
            ParseDateTime(GetString(refund, "updated_at") ?? GetString(refund, "created_at")));
    }

    private async Task<SquareGatewayResponse> SendAsync(HttpMethod method, string path, object? body, string? correlationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Square-Version", _options.SquareVersion);
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.Add("X-Correlation-ID", correlationId);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, SerializerOptions), Encoding.UTF8, "application/json");
        }

        using var client = _httpClientFactory.CreateClient("MasterAppBilling.Square");
        using var response = await client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var providerRequestId = TryGetHeader(response, "x-request-id") ?? TryGetHeader(response, "request-id");

        JsonDocument? json = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                json = JsonDocument.Parse(raw);
            }
            catch (JsonException jsonException)
            {
                _logger.LogWarning(jsonException, "Square returned non-JSON payload for {Path}.", path);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return new SquareGatewayResponse(true, providerRequestId, response.StatusCode.ToString().ToUpperInvariant(), null, null, false, json);
        }

        var squareError = ParseSquareError(json, response.StatusCode);
        _logger.LogWarning(
            "Square request failed. Path={Path} StatusCode={StatusCode} SafeErrorCode={SafeErrorCode} Field={Field} ProviderRequestId={ProviderRequestId}",
            path,
            (int)response.StatusCode,
            squareError.SafeErrorCode,
            squareError.Field,
            providerRequestId);

        return new SquareGatewayResponse(
            false,
            providerRequestId,
            $"HTTP_{(int)response.StatusCode}",
            squareError.SafeErrorCode,
            squareError.SanitizedSummary,
            IsRetryable(response.StatusCode),
            json);
    }

    private static Dictionary<string, object?>? BuildAddress(BillingPostalAddress? address)
    {
        if (address is null)
            return null;

        var line1 = TrimToNull(address.AddressLine1);
        var line2 = TrimToNull(address.AddressLine2);
        var locality = TrimToNull(address.Locality);
        var admin = TrimToNull(address.AdministrativeDistrictLevel1);
        var postal = TrimToNull(address.PostalCode);
        var country = TrimToNull(address.Country);

        if (line1 is null && line2 is null && locality is null && admin is null && postal is null && country is null)
            return null;

        return new Dictionary<string, object?>
        {
            ["address_line_1"] = line1,
            ["address_line_2"] = line2,
            ["locality"] = locality,
            ["administrative_district_level_1"] = admin,
            ["postal_code"] = postal,
            ["country"] = country
        };
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private BillingOneTimePaymentResult? ValidateConfigured(string operationName)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return new BillingOneTimePaymentResult(false, null, "CONFIGURATION_ERROR", "SQUARE_ACCESS_TOKEN_MISSING", $"Square access token is missing for {operationName}.", null, false);
        }

        if (string.IsNullOrWhiteSpace(_options.LocationId))
        {
            return new BillingOneTimePaymentResult(false, null, "CONFIGURATION_ERROR", "SQUARE_LOCATION_ID_MISSING", $"Square location ID is missing for {operationName}.", null, false);
        }

        return null;
    }

    private static SquareError ParseSquareError(JsonDocument? json, HttpStatusCode statusCode)
    {
        if (json?.RootElement.TryGetProperty("errors", out var errors) == true &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var code = GetString(first, "code") ?? $"HTTP_{(int)statusCode}";
            var detail = GetString(first, "detail")
                ?? GetString(first, "category")
                ?? $"Square request failed with HTTP {(int)statusCode}.";
            var field = GetString(first, "field");
            return new SquareError(code, BuildSafeErrorSummary(code, detail, field), field);
        }

        return new SquareError(
            $"HTTP_{(int)statusCode}",
            $"Square request failed with HTTP {(int)statusCode}.",
            null);
    }

    private static string BuildSafeErrorSummary(string code, string detail, string? field)
    {
        if (string.Equals(code, "VALUE_TOO_LONG", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(field, "idempotency_key", StringComparison.OrdinalIgnoreCase))
        {
            return "The payment setup request used an internal identifier that exceeds Square's 45-character limit. No card was charged.";
        }

        return TrimSummary(detail);
    }

    private static string ToSquareIdempotencyKey(string idempotencyKey)
    {
        const int maximumLength = 45;
        var normalized = idempotencyKey.Trim();
        if (normalized.Length <= maximumLength)
            return normalized;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 409 || code == 429 || code >= 500;
    }

    private static string? TryGetHeader(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
            return values.FirstOrDefault();

        if (response.Content.Headers.TryGetValues(headerName, out values))
            return values.FirstOrDefault();

        return null;
    }

    private static string TrimSummary(string value)
    {
        const int maxLength = 280;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? GetString(JsonElement element, string propertyName, string childPropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(child, childPropertyName);
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            return intValue;

        return null;
    }

    private static int? GetInt(JsonElement element, string propertyName, string childPropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return GetInt(child, childPropertyName);
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolValue) => boolValue,
            _ => null
        };
    }

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        return null;
    }

    private sealed record SquareGatewayResponse(
        bool Success,
        string? ProviderRequestId,
        string NormalizedStatus,
        string? SafeErrorCode,
        string? SanitizedSummary,
        bool Retryable,
        JsonDocument? Json);

    private sealed record SquareError(
        string SafeErrorCode,
        string SanitizedSummary,
        string? Field);
}
