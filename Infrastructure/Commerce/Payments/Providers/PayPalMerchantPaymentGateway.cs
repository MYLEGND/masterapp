using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Commerce.Payments.Providers;

public sealed class PayPalMerchantPaymentGateway(IHttpClientFactory clients,IConfiguration configuration) : MerchantProviderTransport(clients,configuration),IMerchantPaymentGateway
{
    public string Provider => "PayPal";
    public MerchantCapabilities Capabilities => MerchantCapabilities.Checkout | MerchantCapabilities.Capture | MerchantCapabilities.Refund | MerchantCapabilities.Reconcile;
    private static readonly string[] SupportedCurrencies=["USD","EUR","GBP","CAD","AUD","NZD"];
    private string Base(string env) => env=="Sandbox"?"https://api-m.sandbox.paypal.com":"https://api-m.paypal.com";
    public bool IsAvailable(string environment) => Enabled(Provider,environment) && WebhookConfigured(Provider,environment,"WebhookId") && Setting(Provider,environment,"ClientId")!="" && Setting(Provider,environment,"ClientSecret")!="" && Setting(Provider,environment,"PartnerMerchantId")!="" && Setting(Provider,environment,"PartnerAttributionId")!="" && (environment=="Sandbox" || Setting(Provider,environment,"PartnerApproved").Equals("true",StringComparison.OrdinalIgnoreCase));
    private async Task<string> Token(string environment,CancellationToken ct)
    {
        Require(IsAvailable(environment),"PAYPAL_PARTNER_NOT_AVAILABLE");
        var basic=Convert.ToBase64String(Encoding.UTF8.GetBytes(Setting(Provider,environment,"ClientId")+":"+Setting(Provider,environment,"ClientSecret")));
        var result=await SendAsync(HttpMethod.Post,Base(environment)+"/v1/oauth2/token","",Form(("grant_type","client_credentials")),ct,("Authorization","Basic "+basic));
        var token=S(result,"access_token");Require(token!="","PROVIDER_AUTHORIZATION_INVALID");return token;
    }
    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private async Task<JsonElement> Api(string env,HttpMethod method,string path,HttpContent? content,CancellationToken ct,string? account=null,string? idempotency=null,bool representation=false)
    {
        var token=await Token(env,ct);var headers=new List<(string,string)>{("PayPal-Partner-Attribution-Id",Setting(Provider,env,"PartnerAttributionId"))};
        if(account is not null) headers.Add(("PayPal-Auth-Assertion",Base64Url("{\"alg\":\"none\"}")+"."+Base64Url(JsonSerializer.Serialize(new {iss=Setting(Provider,env,"ClientId"),payer_id=account}))+"."));
        if(idempotency is not null)headers.Add(("PayPal-Request-Id",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotency)))[..32]));
        if(representation)headers.Add(("Prefer","return=representation"));
        return await SendAsync(method,Base(env)+path,token,content,ct,headers.ToArray());
    }
    public async Task<MerchantAuthorization> BeginConnectionAsync(MerchantConnectionRequest request,CancellationToken ct)
    {
        // This flow is exposed only after authenticated owner consent in the canonical connection authority.
        var tracking=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.State)));
        var result=await Api(request.Environment,HttpMethod.Post,"/v2/customer/partner-referrals",Json(new {tracking_id=tracking,operations=new[]{new {operation="API_INTEGRATION",api_integration_preference=new {rest_api_integration=new {integration_method="PAYPAL",integration_type="THIRD_PARTY",third_party_details=new {features=new[]{"PAYMENT","REFUND"}}}}}},products=new[]{"EXPRESS_CHECKOUT"},legal_consents=new[]{new {type="SHARE_DATA_CONSENT",granted=true}},partner_config_override=new {return_url=request.CallbackUrl+"?state="+Escape(request.State)}}),ct);
        var link=P(result,"links").EnumerateArray().FirstOrDefault(x=>S(x,"rel")=="action_url");Require(link.ValueKind==JsonValueKind.Object && S(link,"href")!="","PROVIDER_INVALID_RESPONSE");return new(S(link,"href"),tracking);
    }
    public async Task<MerchantConnectionResult> CompleteConnectionAsync(string environment,string code,string callbackUrl,string? expectedAccountId,CancellationToken ct)
    {
        Require(!string.IsNullOrEmpty(expectedAccountId),"MERCHANT_IDENTITY_MISSING");
        var result=await Api(environment,HttpMethod.Get,"/v1/customer/partners/"+Escape(Setting(Provider,environment,"PartnerMerchantId"))+"/merchant-integrations?tracking_id="+Escape(expectedAccountId!),null,ct);
        // Tracking identity is issued by the server before authorization, never a browser merchant ID.
        Require(S(result,"tracking_id")==expectedAccountId && S(result,"merchant_id")!="","MERCHANT_IDENTITY_MISMATCH");
        var account=S(result,"merchant_id");
        var verified=await Api(environment,HttpMethod.Get,"/v1/customer/partners/"+Escape(Setting(Provider,environment,"PartnerMerchantId"))+"/merchant-integrations/"+Escape(account),null,ct);
        Require(S(verified,"merchant_id")==account,"MERCHANT_IDENTITY_MISMATCH");
        return new(account,new MerchantCredential(""),GrantedScopes(verified,environment));
    }
    private string[] GrantedScopes(JsonElement result,string environment)
    {
        if(!result.TryGetProperty("oauth_integrations",out var integrations) || integrations.ValueKind!=JsonValueKind.Array)return [];
        return integrations.EnumerateArray()
            .Where(x=>P(x,"oauth_third_party").ValueKind==JsonValueKind.Array)
            .SelectMany(x=>P(x,"oauth_third_party").EnumerateArray())
            .Where(x=>S(x,"partner_client_id")==Setting(Provider,environment,"ClientId") && P(x,"scopes").ValueKind==JsonValueKind.Array)
            .SelectMany(x=>P(x,"scopes").EnumerateArray()).Select(x=>x.GetString() ?? "").Distinct().ToArray();
    }
    public async Task<MerchantReadiness> GetReadinessAsync(MerchantAccount account,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/v1/customer/partners/"+Escape(Setting(Provider,account.Environment,"PartnerMerchantId"))+"/merchant-integrations/"+Escape(account.AccountId),null,ct);
        Require(S(result,"merchant_id")==account.AccountId,"MERCHANT_IDENTITY_MISMATCH");
        var granted=GrantedScopes(result,account.Environment).Contains("https://uri.paypal.com/services/payments/payment/authcapture",StringComparer.Ordinal);
        // Merchant Integrations does not guarantee a currency/balance inventory. This is the
        // adapter's documented PayPal currency subset, not a claim about merchant balances.
        // Receiving preferences can leave a payment pending; only a verified COMPLETED capture
        // becomes revenue. Never gate otherwise eligible onboarding on optional primary_currency.
        var ready=B(result,"payments_receivable") && B(result,"primary_email_confirmed") && granted;
        return new(ready,ready,SupportedCurrencies,["paypal"],[],ready?null:"Complete PayPal onboarding, confirm email, and grant payment permissions.");
    }
    public Task<MerchantCredential> RefreshAsync(MerchantAccount account,CancellationToken ct)=>Task.FromResult(account.Credential);
    public Task RevokeAsync(MerchantAccount account,CancellationToken ct)=>throw new MerchantPaymentException("PROVIDER_REVOKE_UNSUPPORTED_USE_DASHBOARD");
    private static string Money(long amount,string currency)
    {
        Require(SupportedCurrencies.Contains(currency,StringComparer.Ordinal),"PROVIDER_CURRENCY_UNSUPPORTED");
        return (amount/100m).ToString("F2",CultureInfo.InvariantCulture);
    }
    private static long Minor(JsonElement amount)
    {
        Require(SupportedCurrencies.Contains(S(amount,"currency_code"),StringComparer.Ordinal),"PROVIDER_CURRENCY_UNSUPPORTED");
        Require(decimal.TryParse(S(amount,"value"),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value) && value>=0 && value*100==decimal.Truncate(value*100),"PROVIDER_AMOUNT_INVALID");
        return checked((long)(value*100));
    }
    public async Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount account,MerchantCheckoutRequest request,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Post,"/v2/checkout/orders",Json(new {intent="CAPTURE",purchase_units=new[]{new {reference_id=request.OrderReference,custom_id=request.AttemptId.ToString("D"),amount=new {currency_code=request.Currency,value=Money(request.AmountMinor,request.Currency)},payee=new {merchant_id=account.AccountId}}},payment_source=new {paypal=new {experience_context=new {return_url=request.ReturnUrl,cancel_url=request.ReturnUrl,user_action="PAY_NOW"}}}}),ct,account.AccountId,request.IdempotencyKey);
        var link=P(result,"links").EnumerateArray().FirstOrDefault(x=>S(x,"rel") is "payer-action" or "approve");
        Require(S(result,"id")!="" && link.ValueKind==JsonValueKind.Object,"PROVIDER_INVALID_RESPONSE");
        return new(account.AccountId,account.Environment,S(result,"id"),null,"Pending",request.AmountMinor,request.Currency,CheckoutUrl:S(link,"href"));
    }
    public async Task<MerchantPaymentEvidence> GetPaymentAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/v2/checkout/orders/"+Escape(reference),null,ct,account.AccountId);
        Require(S(result,"id")==reference,"PROVIDER_REFERENCE_MISMATCH");
        var units=P(result,"purchase_units").EnumerateArray().ToArray();Require(units.Length==1,"PROVIDER_AMOUNT_INVALID");var unit=units[0];
        Require(S(P(unit,"payee"),"merchant_id")==account.AccountId,"MERCHANT_IDENTITY_MISMATCH");
        var amount=P(unit,"amount");var total=Minor(amount);var currency=S(amount,"currency_code");var state=S(result,"status")=="APPROVED"?"Approved":"Pending";string? captureId=null;long refunded=0;
        var payments=P(unit,"payments");
        if(payments.ValueKind==JsonValueKind.Object && payments.TryGetProperty("captures",out var captures))
        {
            var entries=captures.EnumerateArray().ToArray();Require(entries.Length<=1,"PROVIDER_MULTIPLE_CAPTURES_REVIEW_REQUIRED");
            if(entries.Length==1)
            {
                var capture=await Api(account.Environment,HttpMethod.Get,"/v2/payments/captures/"+Escape(S(entries[0],"id")),null,ct,account.AccountId);
                Require(Minor(P(capture,"amount"))==total && S(P(capture,"amount"),"currency_code")==currency,"PAYMENT_AMOUNT_MISMATCH");
                captureId=S(capture,"id");var status=S(capture,"status");
                if(status is "COMPLETED" or "PARTIALLY_REFUNDED" or "REFUNDED")state="Captured";
                if(status=="REFUNDED")refunded=total;
                if(status=="PARTIALLY_REFUNDED")
                {
                    Require(payments.TryGetProperty("refunds",out var refunds) && refunds.ValueKind==JsonValueKind.Array,"PAYPAL_PARTIAL_REFUND_RECONCILIATION_REQUIRED");
                    foreach(var refund in refunds.EnumerateArray())
                    {
                        var evidence=await Api(account.Environment,HttpMethod.Get,"/v2/payments/refunds/"+Escape(S(refund,"id")),null,ct,account.AccountId);
                        Require(S(P(evidence,"amount"),"currency_code")==currency,"PAYMENT_AMOUNT_MISMATCH");
                        if(S(evidence,"status")=="COMPLETED")refunded=checked(refunded+Minor(P(evidence,"amount")));
                    }
                    Require(refunded>0 && refunded<total,"PAYPAL_PARTIAL_REFUND_RECONCILIATION_REQUIRED");
                }
            }
        }
        return new(account.AccountId,account.Environment,reference,captureId,state,total,currency,refunded);
    }
    public async Task<MerchantPaymentEvidence> CaptureAsync(MerchantAccount account,string reference,string idempotencyKey,CancellationToken ct)
    {
        var before=await GetPaymentAsync(account,reference,ct);if(before.State=="Captured")return before;Require(before.State=="Approved","PAYMENT_NOT_APPROVED");
        await Api(account.Environment,HttpMethod.Post,"/v2/checkout/orders/"+Escape(reference)+"/capture",Json(new {}),ct,account.AccountId,idempotencyKey);
        return await GetPaymentAsync(account,reference,ct);
    }
    public async Task<MerchantRefundResult> RefundAsync(MerchantAccount account,MerchantRefundRequest request,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Post,"/v2/payments/captures/"+Escape(request.PaymentReference)+"/refund",Json(new {amount=new {currency_code=request.Currency,value=Money(request.AmountMinor,request.Currency)}}),ct,account.AccountId,request.IdempotencyKey,representation:true);
        // Default PayPal POST responses are minimal. Request the documented full resource;
        // if a provider response remains minimal, read back its exact ID without reposting.
        Require(S(result,"id")!="","PROVIDER_REFERENCE_MISSING");
        if(P(result,"amount").ValueKind!=JsonValueKind.Object)
            return await GetRefundAsync(account,S(result,"id"),ct);
        return RefundResult(result);
    }
    private static MerchantRefundResult RefundResult(JsonElement result)
    {
        string? payment=null;
        if(result.TryGetProperty("links",out var links))
        {
            var up=links.EnumerateArray().FirstOrDefault(x=>S(x,"rel")=="up");
            if(up.ValueKind==JsonValueKind.Object && Uri.TryCreate(S(up,"href"),UriKind.Absolute,out var uri) && uri.AbsolutePath.StartsWith("/v2/payments/captures/",StringComparison.Ordinal))
            {
                var id=uri.AbsolutePath["/v2/payments/captures/".Length..];if(!id.Contains('/'))payment=id;
            }
        }
        return new(S(result,"id"),S(result,"status") switch {"COMPLETED"=>"Succeeded","PENDING"=>"Pending","FAILED" or "CANCELLED"=>"Failed",_=>"Unknown"},payment,Minor(P(result,"amount")),S(P(result,"amount"),"currency_code"));
    }
    public async Task<MerchantRefundResult> GetRefundAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/v2/payments/refunds/"+Escape(reference),null,ct,account.AccountId);
        Require(S(result,"id")==reference,"PROVIDER_REFERENCE_MISMATCH");return RefundResult(result);
    }
    public async Task<MerchantWebhook?> VerifyWebhookAsync(string environment,string body,IReadOnlyDictionary<string,string> headers,string endpointUrl,CancellationToken ct)
    {
        var webhookId=Setting(Provider,environment,"WebhookId");if(webhookId=="")return null;
        var transmission=Header(headers,"PAYPAL-TRANSMISSION-ID");var signature=Header(headers,"PAYPAL-TRANSMISSION-SIG");if(transmission=="" || signature=="")return null;
        using var doc=JsonDocument.Parse(body);var root=doc.RootElement;
        var verified=await Api(environment,HttpMethod.Post,"/v1/notifications/verify-webhook-signature",Json(new {auth_algo=Header(headers,"PAYPAL-AUTH-ALGO"),cert_url=Header(headers,"PAYPAL-CERT-URL"),transmission_id=transmission,transmission_sig=signature,transmission_time=Header(headers,"PAYPAL-TRANSMISSION-TIME"),webhook_id=webhookId,webhook_event=root}),ct);
        if(S(verified,"verification_status")!="SUCCESS")return null;
        var resource=P(root,"resource");var reference=S(resource,"id");var type=S(root,"event_type");string account="";
        if(type.StartsWith("CHECKOUT.ORDER.",StringComparison.Ordinal))
        {
            if(!resource.TryGetProperty("purchase_units",out var units))return null;var values=units.EnumerateArray().ToArray();if(values.Length!=1)return null;account=S(P(values[0],"payee"),"merchant_id");
        }
        else
        {
            var related=P(P(resource,"supplementary_data"),"related_ids");reference=related.ValueKind==JsonValueKind.Object?S(related,"order_id"):"";
            if(resource.TryGetProperty("payee",out var payee))account=S(payee,"merchant_id");
            if(reference=="" && related.ValueKind==JsonValueKind.Object && S(related,"capture_id")!="")
            {
                var capture=await Api(environment,HttpMethod.Get,"/v2/payments/captures/"+Escape(S(related,"capture_id")),null,ct);
                reference=S(P(P(capture,"supplementary_data"),"related_ids"),"order_id");
            }
            if(reference=="" && resource.TryGetProperty("links",out var resourceLinks))
            {
                var up=resourceLinks.EnumerateArray().FirstOrDefault(x=>S(x,"rel")=="up");
                if(up.ValueKind==JsonValueKind.Object && Uri.TryCreate(S(up,"href"),UriKind.Absolute,out var upUri) && upUri.AbsolutePath.StartsWith("/v2/payments/captures/",StringComparison.Ordinal))
                {
                    // Use only the escaped identifier; never fetch a webhook-supplied URL.
                    var captureId=upUri.AbsolutePath["/v2/payments/captures/".Length..];
                    if(!captureId.Contains('/'))
                    {
                        var capture=await Api(environment,HttpMethod.Get,"/v2/payments/captures/"+Escape(captureId),null,ct);
                        reference=S(P(P(capture,"supplementary_data"),"related_ids"),"order_id");
                    }
                }
            }
            if(reference=="")return null;
            if(account=="")
            {
                var order=await Api(environment,HttpMethod.Get,"/v2/checkout/orders/"+Escape(reference),null,ct);
                var units=P(order,"purchase_units").EnumerateArray().ToArray();if(units.Length!=1)return null;
                account=S(P(units[0],"payee"),"merchant_id");
            }
        }
        if(reference=="")return null;
        var original=await Api(environment,HttpMethod.Get,"/v2/checkout/orders/"+Escape(reference),null,ct);
        Require(S(original,"id")==reference,"PROVIDER_REFERENCE_MISMATCH");
        var originalUnits=P(original,"purchase_units").EnumerateArray().ToArray();if(originalUnits.Length!=1)return null;
        var merchant=S(P(originalUnits[0],"payee"),"merchant_id");
        if(merchant=="" || (account!="" && account!=merchant))return null;
        Guid? attempt=Guid.TryParse(S(originalUnits[0],"custom_id"),out var parsed)?parsed:null;
        return new(S(root,"id"),merchant,environment,reference,DateTime.Parse(S(root,"create_time")).ToUniversalTime(),attempt);
    }
}
