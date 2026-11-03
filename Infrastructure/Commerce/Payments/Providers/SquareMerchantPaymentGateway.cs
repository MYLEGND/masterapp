using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Commerce.Payments.Providers;

public sealed class SquareMerchantPaymentGateway(IHttpClientFactory clients, IConfiguration configuration) : MerchantProviderTransport(clients, configuration), IMerchantPaymentGateway
{
    public string Provider => "Square";
    public MerchantCapabilities Capabilities => MerchantCapabilities.Checkout | MerchantCapabilities.Refund | MerchantCapabilities.Reconcile | MerchantCapabilities.Revoke;
    private const string Scopes = "MERCHANT_PROFILE_READ PAYMENTS_READ PAYMENTS_WRITE ORDERS_READ ORDERS_WRITE";
    private string Base(string env) => env == "Sandbox" ? "https://connect.squareupsandbox.com" : "https://connect.squareup.com";
    public bool IsAvailable(string environment) => Enabled(Provider,environment) && WebhookConfigured(Provider,environment,"WebhookSignatureKey") && Setting(Provider,environment,"ApplicationId") != "" && Setting(Provider,environment,"ApplicationSecret") != "";
    private Task<JsonElement> Api(string env, HttpMethod method, string path, string token, HttpContent? body, CancellationToken ct) => SendAsync(method,Base(env)+path,token,body,ct,("Square-Version","2026-09-16"));
    public Task<MerchantAuthorization> BeginConnectionAsync(MerchantConnectionRequest request, CancellationToken ct)
    {
        Require(IsAvailable(request.Environment),"PROVIDER_NOT_AVAILABLE");
        return Task.FromResult(new MerchantAuthorization(Base(request.Environment)+"/oauth2/authorize?client_id="+Escape(Setting(Provider,request.Environment,"ApplicationId"))+"&scope="+Escape(Scopes)+"&session=false&state="+Escape(request.State)+"&redirect_uri="+Escape(request.CallbackUrl)));
    }
    private async Task<MerchantConnectionResult> Token(string env, object body, CancellationToken ct)
    {
        var result = await Api(env,HttpMethod.Post,"/oauth2/token","",Json(body),ct);
        var account = S(result,"merchant_id"); var access = S(result,"access_token");
        Require(account != "" && access != "","PROVIDER_AUTHORIZATION_INVALID");
        DateTime? expires = DateTime.TryParse(S(result,"expires_at"), out var expiry) ? expiry.ToUniversalTime() : null;
        var status = await Api(env,HttpMethod.Post,"/oauth2/token/status",access,Json(new {}),ct);
        Require(S(status,"merchant_id")==account && S(status,"client_id")==Setting(Provider,env,"ApplicationId"),"MERCHANT_IDENTITY_MISMATCH");
        var scopes=P(status,"scopes").EnumerateArray().Select(x=>x.GetString() ?? "").ToArray();
        Require(Scopes.Split(' ').All(scope=>scopes.Contains(scope,StringComparer.Ordinal)),"MERCHANT_PERMISSIONS_INCOMPLETE");
        return new(account,new(access,S(result,"refresh_token"),expires),scopes);
    }
    public async Task<MerchantConnectionResult> CompleteConnectionAsync(string environment, string code, string callbackUrl, string? expectedAccountId, CancellationToken ct)
    {
        Require(IsAvailable(environment),"PROVIDER_NOT_AVAILABLE");
        var result = await Token(environment,new { client_id=Setting(Provider,environment,"ApplicationId"),client_secret=Setting(Provider,environment,"ApplicationSecret"),code,grant_type="authorization_code",redirect_uri=callbackUrl },ct);
        Require(expectedAccountId is null || result.AccountId == expectedAccountId,"MERCHANT_IDENTITY_MISMATCH"); return result;
    }
    public async Task<MerchantCredential> RefreshAsync(MerchantAccount account, CancellationToken ct)
    {
        Require(!string.IsNullOrEmpty(account.Credential.RefreshToken),"RECONNECT_REQUIRED");
        var result = await Token(account.Environment,new { client_id=Setting(Provider,account.Environment,"ApplicationId"),client_secret=Setting(Provider,account.Environment,"ApplicationSecret"),refresh_token=account.Credential.RefreshToken,grant_type="refresh_token" },ct);
        Require(result.AccountId == account.AccountId,"MERCHANT_IDENTITY_MISMATCH"); return result.Credential;
    }
    public async Task<MerchantAccount> ResolvePlatformAccountAsync(string environment, CancellationToken ct)
    {
        var options = Infrastructure.Billing.Square.SquareBillingOptions.FromConfiguration(Configuration);
        Require(options.Environment.ToString() == environment && options.HasServerCredentials() && options.HasBrowserCredentials(), "PLATFORM_PAYMENT_CONFIGURATION_UNAVAILABLE");
        Require(options.ApplicationId == Setting(Provider, environment, "ApplicationId"), "PLATFORM_APPLICATION_MISMATCH");
        var response = await Api(environment, HttpMethod.Get, "/v2/locations", options.AccessToken!, null, ct);
        var locations = P(response, "locations");
        Require(locations.ValueKind == JsonValueKind.Array, "PROVIDER_INVALID_RESPONSE");
        var matches = locations.EnumerateArray().Where(x => S(x, "id") == options.LocationId).ToArray();
        Require(matches.Length == 1 && S(matches[0], "merchant_id") != "", "MERCHANT_IDENTITY_MISMATCH");
        var account = new MerchantAccount(Provider, environment, S(matches[0], "merchant_id"), options.LocationId,
            new MerchantCredential(options.AccessToken!, Source: "PlatformBilling", ApplicationId: options.ApplicationId));
        var readiness = await GetReadinessAsync(account, ct);
        Require(readiness.CanAcceptPayments(Provider) && readiness.Locations.Contains(options.LocationId!), "MERCHANT_NOT_READY");
        return account;
    }
    public async Task<MerchantReadiness> GetReadinessAsync(MerchantAccount account, CancellationToken ct)
    {
        var merchant = await Api(account.Environment,HttpMethod.Get,"/v2/merchants/"+Escape(account.AccountId),account.Credential.AccessToken,null,ct);
        Require(S(P(merchant,"merchant"),"id") == account.AccountId,"MERCHANT_IDENTITY_MISMATCH");
        var locations = await Api(account.Environment,HttpMethod.Get,"/v2/locations",account.Credential.AccessToken,null,ct);
        var list = P(locations, "locations");
        if (list.ValueKind != JsonValueKind.Array)
            return new(null, null, [], [], [], "Square location readiness could not be verified. Refresh readiness.");
        bool? Capability(JsonElement location, string name)
        {
            var caps = P(location, "capabilities");
            return caps.ValueKind == JsonValueKind.Array && caps.EnumerateArray().All(c => c.ValueKind == JsonValueKind.String)
                ? caps.EnumerateArray().Any(c => c.GetString() == name) : null;
        }
        var owned = list.EnumerateArray().Where(x => S(x, "merchant_id") == account.AccountId).ToArray();
        var selectedLocations = list.EnumerateArray().Where(x => S(x, "id") == account.LocationId && !string.IsNullOrEmpty(account.LocationId)).ToArray();
        Require(selectedLocations.Length <= 1, "PROVIDER_INVALID_RESPONSE");
        var selected = selectedLocations.FirstOrDefault();
        if (selected.ValueKind != JsonValueKind.Undefined)
            Require(S(selected, "merchant_id") == account.AccountId, "MERCHANT_IDENTITY_MISMATCH");
        var eligible = owned.Where(x => S(x, "status") == "ACTIVE" && Capability(x, "CREDIT_CARD_PROCESSING") == true &&
            !string.IsNullOrWhiteSpace(S(x, "id")) && !string.IsNullOrWhiteSpace(S(x, "currency"))).ToArray();
        bool? payments = null;
        var merchantStatus = S(P(merchant, "merchant"), "status");
        var locationStatus = S(selected, "status");
        if (string.IsNullOrEmpty(account.LocationId) || merchantStatus == "INACTIVE" || locationStatus == "INACTIVE")
            payments = false;
        else if (merchantStatus == "ACTIVE" && locationStatus == "ACTIVE" && !string.IsNullOrWhiteSpace(S(selected, "currency")))
            payments = Capability(selected, "CREDIT_CARD_PROCESSING");
        var transfers = selected.ValueKind == JsonValueKind.Undefined ? null : Capability(selected, "AUTOMATIC_TRANSFERS");
        return new(payments, null,
            payments == true ? [S(selected, "currency")] : eligible.Select(x => S(x, "currency")).Distinct().ToArray(),
            payments == true ? ["card"] : [], eligible.Select(x => S(x, "id")).ToArray(),
            payments == true ? null : payments == false ? "Select an active card-processing location." : "Square payment readiness could not be verified. Refresh readiness.",
            transfers);
    }
    public async Task RevokeAsync(MerchantAccount account,CancellationToken ct) => await SendAsync(HttpMethod.Post,Base(account.Environment)+"/oauth2/revoke","",Json(new {client_id=Setting(Provider,account.Environment,"ApplicationId"),access_token=account.Credential.AccessToken,revoke_only_access_token=false}),ct,("Authorization","Client "+Setting(Provider,account.Environment,"ApplicationSecret")));
    public MerchantBrowserConfiguration BrowserConfiguration(MerchantAccount account, Guid revisionId)
    {
        var appId = Setting(Provider, account.Environment, "ApplicationId");
        Require(appId != "" && !string.IsNullOrWhiteSpace(account.LocationId), "MERCHANT_NOT_READY");
        Require(account.Credential.ApplicationId is null || account.Credential.ApplicationId == appId, "PLATFORM_APPLICATION_MISMATCH");
        return new(Provider, account.Environment, appId, account.LocationId!, revisionId);
    }

    public async Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount account, MerchantCheckoutRequest request, CancellationToken ct)
    {
        Require(!string.IsNullOrWhiteSpace(account.LocationId), "LOCATION_REQUIRED");
        Require(!string.IsNullOrWhiteSpace(request.SourceToken), "PAYMENT_TOKEN_REQUIRED");
        Require(!string.Equals(request.SourceToken?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.SourceToken?.Trim(), "EXTERNAL", StringComparison.OrdinalIgnoreCase), "PAYMENT_TOKEN_INVALID");
        // Both keys derive from the already committed durable attempt. Tokens are request-only.
        Require(request.IdempotencyKey.Length <= 40, "PAYMENT_IDEMPOTENCY_INVALID");
        var created = await Api(account.Environment, HttpMethod.Post, "/v2/orders", account.Credential.AccessToken,
            Json(new { idempotency_key = request.IdempotencyKey + "-ord", order = new {
                location_id = account.LocationId, reference_id = request.OrderReference,
                metadata = new { attempt_id = request.AttemptId.ToString("D") },
                line_items = new[] { new { name = "Order " + request.OrderReference, quantity = "1",
                    base_price_money = new { amount = request.AmountMinor, currency = request.Currency } } }
            } }), ct);
        var order = P(created, "order");
        var orderId = S(order, "id");
        Require(orderId != "" && S(order, "location_id") == account.LocationId, "MERCHANT_IDENTITY_MISMATCH");
        Require(L(P(order, "total_money"), "amount") == request.AmountMinor && S(P(order, "total_money"), "currency") == request.Currency, "PAYMENT_AMOUNT_MISMATCH");
        try
        {
        await Api(account.Environment, HttpMethod.Post, "/v2/payments", account.Credential.AccessToken,
            Json(new { source_id = request.SourceToken, idempotency_key = request.IdempotencyKey + "-pay",
                order_id = orderId, location_id = account.LocationId, reference_id = request.AttemptId.ToString("N"),
                amount_money = new { amount = request.AmountMinor, currency = request.Currency }, autocomplete = true,
                buyer_email_address = request.Customer?.Email,
                billing_address = request.Customer is null ? null : new { first_name = request.Customer.FirstName,
                    last_name = request.Customer.LastName, address_line_1 = request.Customer.AddressLine1,
                    address_line_2 = request.Customer.AddressLine2, locality = request.Customer.City,
                    administrative_district_level_1 = request.Customer.State, postal_code = request.Customer.PostalCode, country = "US" }
            }), ct);
        }
        catch (MerchantPaymentException ex) when (ex.Message == "PAYMENT_DECLINED")
        {
            return new(account.AccountId, account.Environment, orderId, null, "Failed", request.AmountMinor, request.Currency);
        }
        // Existing order/tender reconciliation remains the only capture evidence authority.
        return await GetPaymentAsync(account, orderId, ct);
    }
    protected override bool IsDefinitivePaymentDecline(HttpRequestMessage request, System.Net.HttpStatusCode status, JsonElement body)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v2/payments" ||
            status != System.Net.HttpStatusCode.BadRequest || !body.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return false;
        // Only explicit card rejection is terminal; malformed, mixed and ambiguous responses stay held.
        return errors.EnumerateArray().All(error => S(error, "category") == "PAYMENT_METHOD_ERROR" &&
            S(error, "code") is "CARD_DECLINED" or "GENERIC_DECLINE" or "INSUFFICIENT_FUNDS" or
                "CVV_FAILURE" or "ADDRESS_VERIFICATION_FAILURE" or "CARD_EXPIRED");
    }

    public async Task<MerchantPaymentEvidence?> FindCheckoutAsync(MerchantAccount account,MerchantCheckoutRequest request,CancellationToken ct)
    {
        Require(!string.IsNullOrWhiteSpace(account.LocationId),"LOCATION_REQUIRED");
        string? cursor=null;
        for(var page=0;page<3;page++)
        {
            var result=await Api(account.Environment,HttpMethod.Post,"/v2/orders/search",account.Credential.AccessToken,Json(new {location_ids=new[]{account.LocationId},cursor,limit=100,query=new {sort=new {sort_field="CREATED_AT",sort_order="DESC"}}}),ct);
            if(!result.TryGetProperty("orders",out var orders))return null;
            var matches=orders.EnumerateArray().Where(x=>S(x,"reference_id")==request.OrderReference && S(P(x,"metadata"),"attempt_id")==request.AttemptId.ToString("D")).ToArray();Require(matches.Length<=1,"AMBIGUOUS_PROVIDER_CHECKOUT");
            if(matches.Length==1)
            {
                var evidence=await GetPaymentAsync(account,S(matches[0],"id"),ct);
                Require(evidence.AmountMinor==request.AmountMinor && evidence.Currency==request.Currency,"PAYMENT_AMOUNT_MISMATCH");
                return evidence with { CheckoutUrl=await FindPaymentLinkAsync(account,evidence.Reference,ct) };
            }
            cursor=S(result,"cursor");if(cursor=="")return null;
        }
        return null;
    }
    private async Task<string?> FindPaymentLinkAsync(MerchantAccount account,string orderId,CancellationToken ct)
    {
        string? cursor=null;
        for(var page=0;page<3;page++)
        {
            var result=await Api(account.Environment,HttpMethod.Get,"/v2/online-checkout/payment-links?limit=100"+(cursor is null?"":"&cursor="+Escape(cursor)),account.Credential.AccessToken,null,ct);
            if(!result.TryGetProperty("payment_links",out var links))return null;
            var matches=links.EnumerateArray().Where(x=>S(x,"order_id")==orderId).ToArray();
            Require(matches.Length<=1,"AMBIGUOUS_PROVIDER_CHECKOUT");
            if(matches.Length==1)return S(matches[0],"url");
            cursor=S(result,"cursor");if(cursor=="")return null;
        }
        return null;
    }
    public async Task<MerchantPaymentEvidence> GetPaymentAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result = await Api(account.Environment,HttpMethod.Get,"/v2/orders/"+Escape(reference),account.Credential.AccessToken,null,ct); var order=P(result,"order");
        Require(S(order,"id")==reference && S(order,"location_id")==account.LocationId,"MERCHANT_IDENTITY_MISMATCH");
        var money=P(order,"total_money"); var state="Pending"; string? paymentId=null; long refunded=0;
        if(order.TryGetProperty("tenders",out var tenders)) foreach(var tender in tenders.EnumerateArray())
        {
            var id=S(tender,"payment_id"); if(id=="")continue;
            var payment=P(await Api(account.Environment,HttpMethod.Get,"/v2/payments/"+Escape(id),account.Credential.AccessToken,null,ct),"payment");
            Require(S(payment,"order_id")==reference && S(payment,"location_id")==account.LocationId,"MERCHANT_IDENTITY_MISMATCH");
            if(S(payment,"status")=="COMPLETED") { Require(L(P(payment,"amount_money"),"amount")==L(money,"amount") && S(P(payment,"amount_money"),"currency")==S(money,"currency"),"PAYMENT_AMOUNT_MISMATCH"); state="Captured";paymentId=id;refunded=L(P(payment,"refunded_money"),"amount");break; }
        }
        return new(account.AccountId,account.Environment,reference,paymentId,state,L(money,"amount"),S(money,"currency"),refunded);
    }
    public async Task<MerchantRefundResult> RefundAsync(MerchantAccount account,MerchantRefundRequest request,CancellationToken ct)
    {
        var result=P(await Api(account.Environment,HttpMethod.Post,"/v2/refunds",account.Credential.AccessToken,Json(new {idempotency_key=request.IdempotencyKey,payment_id=request.PaymentReference,amount_money=new {amount=request.AmountMinor,currency=request.Currency}}),ct),"refund");
        return RefundResult(result);
    }
    private static MerchantRefundResult RefundResult(JsonElement result) => new(S(result,"id"),S(result,"status") switch {"COMPLETED"=>"Succeeded","PENDING"=>"Pending","REJECTED" or "FAILED"=>"Failed",_=>"Unknown"},S(result,"payment_id"),L(P(result,"amount_money"),"amount"),S(P(result,"amount_money"),"currency"));
    public async Task<MerchantRefundResult> GetRefundAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result=P(await Api(account.Environment,HttpMethod.Get,"/v2/refunds/"+Escape(reference),account.Credential.AccessToken,null,ct),"refund");
        Require(S(result,"id")==reference,"PROVIDER_REFERENCE_MISMATCH");return RefundResult(result);
    }
    public async Task<MerchantWebhook?> VerifyWebhookAsync(string environment,string body,IReadOnlyDictionary<string,string> headers,string endpointUrl,CancellationToken ct)
    {
        if(!PaymentWebhookSignatures.VerifySquare(body,Header(headers,"x-square-hmacsha256-signature"),Setting(Provider,environment,"WebhookSignatureKey"),endpointUrl))return null;
        using var doc=JsonDocument.Parse(body);var root=doc.RootElement;var obj=P(P(root,"data"),"object");var payment=P(obj,"payment");
        // A refund has its own order. Match its original payment instead; the
        // canonical checkout owner scopes that reference to merchant/environment.
        var reference=payment.ValueKind==JsonValueKind.Object?S(payment,"order_id"):S(P(obj,"refund"),"payment_id");
        if(reference=="")return null;
        await Task.CompletedTask;
        return new(S(root,"event_id"),S(root,"merchant_id"),environment,reference,DateTime.Parse(S(root,"created_at")).ToUniversalTime());
    }
}
