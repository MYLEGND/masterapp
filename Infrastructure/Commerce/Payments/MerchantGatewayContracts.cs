namespace Infrastructure.Commerce.Payments;

[Flags]
public enum MerchantCapabilities { None = 0, Checkout = 1, Refund = 2, Capture = 4, Reconcile = 8, Revoke = 16, Disputes = 32 }
public sealed record MerchantCredential(string AccessToken, string? RefreshToken = null, DateTime? ExpiresUtc = null);
public sealed record MerchantAccount(string Provider, string Environment, string AccountId, string? LocationId, MerchantCredential Credential);
public sealed record MerchantConnectionRequest(Guid BusinessId, string Environment, string State, string CallbackUrl, string? ExistingAccountId = null);
public sealed record MerchantAuthorization(string RedirectUrl, string? AccountId = null);
public sealed record MerchantConnectionResult(string AccountId, MerchantCredential Credential, string[] GrantedScopes);
// Capabilities are provider observations, never receipts for completed bank payouts.
public sealed record MerchantReadiness(bool? PaymentsEnabled, bool? SettlementsEnabled, string[] Currencies, string[] PaymentMethods, string[] Locations, string? RequiredAction = null, bool? AutomaticTransfersEnabled = null)
{
    public bool CanAcceptPayments(string provider) => PaymentsEnabled == true &&
        (provider == "Square" || SettlementsEnabled == true);
}
public sealed record MerchantCheckoutRequest(Guid AttemptId, string IdempotencyKey, long AmountMinor, string Currency, string OrderReference, string ReturnUrl, string? SourceToken = null);
public sealed record MerchantPaymentEvidence(string AccountId, string Environment, string Reference, string? PaymentReference, string State, long AmountMinor, string Currency, long RefundedMinor = 0, string? CheckoutUrl = null, string? DisputeState = null);
public sealed record MerchantWebhook(string EventId, string AccountId, string Environment, string Reference, DateTime OccurredUtc, Guid? AttemptId = null);
public sealed record MerchantRefundRequest(string PaymentReference, long AmountMinor, string Currency, string IdempotencyKey);
public sealed record MerchantRefundResult(string Reference, string State, string? PaymentReference = null, long? AmountMinor = null, string? Currency = null);
public interface IMerchantPaymentGateway
{
    string Provider { get; }
    MerchantCapabilities Capabilities { get; }
    bool IsAvailable(string environment);
    Task<MerchantAuthorization> BeginConnectionAsync(MerchantConnectionRequest request, CancellationToken ct);
    Task<MerchantConnectionResult> CompleteConnectionAsync(string environment, string code, string callbackUrl, string? expectedAccountId, CancellationToken ct);
    Task<MerchantReadiness> GetReadinessAsync(MerchantAccount account, CancellationToken ct);
    Task<MerchantCredential> RefreshAsync(MerchantAccount account, CancellationToken ct);
    Task RevokeAsync(MerchantAccount account, CancellationToken ct);
    Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount account, MerchantCheckoutRequest request, CancellationToken ct);
    Task<MerchantPaymentEvidence> GetPaymentAsync(MerchantAccount account, string reference, CancellationToken ct);
    Task<MerchantPaymentEvidence?> FindCheckoutAsync(MerchantAccount account, MerchantCheckoutRequest request, CancellationToken ct) => Task.FromResult<MerchantPaymentEvidence?>(null);
    Task<MerchantPaymentEvidence> CaptureAsync(MerchantAccount account, string reference, string idempotencyKey, CancellationToken ct) => throw new MerchantPaymentException("PROVIDER_CAPTURE_UNSUPPORTED");
    Task<MerchantRefundResult> RefundAsync(MerchantAccount account, MerchantRefundRequest request, CancellationToken ct);
    Task<MerchantRefundResult> GetRefundAsync(MerchantAccount account, string reference, CancellationToken ct) => throw new MerchantPaymentException("PROVIDER_REFUND_RECONCILIATION_UNSUPPORTED");
    Task<MerchantWebhook?> VerifyWebhookAsync(string environment, string body, IReadOnlyDictionary<string,string> headers, string endpointUrl, CancellationToken ct);
}
public sealed class MerchantPaymentException(string safeCode) : Exception(safeCode);
