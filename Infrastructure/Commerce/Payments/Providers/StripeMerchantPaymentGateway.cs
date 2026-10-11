using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Commerce.Payments.Providers;

public sealed class StripeMerchantPaymentGateway(IHttpClientFactory clients, IConfiguration configuration) : MerchantProviderTransport(clients, configuration), IMerchantPaymentGateway
{
    public string Provider => "Stripe";
    public MerchantCapabilities Capabilities => MerchantCapabilities.Checkout | MerchantCapabilities.Refund | MerchantCapabilities.Reconcile;
    public bool IsAvailable(string environment) => Enabled(Provider,environment) && WebhookConfigured(Provider,environment,"WebhookSecret") && Setting(Provider,environment,"SecretKey").StartsWith(environment == "Production" ? "sk_live_" : "sk_test_",StringComparison.Ordinal);
    private Task<JsonElement> Api(string env,HttpMethod method,string path,HttpContent? content,CancellationToken ct,string? account=null,string? idempotency=null)
    {
        Require(IsAvailable(env),"PROVIDER_NOT_AVAILABLE");
        var headers=new List<(string,string)>(); if(account is not null)headers.Add(("Stripe-Account",account)); if(idempotency is not null)headers.Add(("Idempotency-Key",idempotency));
        return SendAsync(method,"https://api.stripe.com/v1"+path,Setting(Provider,env,"SecretKey"),content,ct,headers.ToArray());
    }
    public async Task<MerchantAuthorization> BeginConnectionAsync(MerchantConnectionRequest request,CancellationToken ct)
    {
        var account=request.ExistingAccountId;
        if(string.IsNullOrEmpty(account))
        {
            var created=await Api(request.Environment,HttpMethod.Post,"/accounts",Form(("type","standard"),("metadata[commerce_business_id]",request.BusinessId.ToString("D"))),ct,idempotency:"merchant-account-"+request.BusinessId.ToString("N")+"-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.State))));
            account=S(created,"id"); Require(account.StartsWith("acct_",StringComparison.Ordinal),"PROVIDER_INVALID_RESPONSE");
        }
        var link=await Api(request.Environment,HttpMethod.Post,"/account_links",Form(("account",account),("type","account_onboarding"),("return_url",request.CallbackUrl+"?state="+Escape(request.State)),("refresh_url",request.CallbackUrl+"?state="+Escape(request.State)+"&refresh=true")),ct);
        Require(S(link,"url")!="","PROVIDER_INVALID_RESPONSE"); return new(S(link,"url"),account);
    }
    public async Task<MerchantConnectionResult> CompleteConnectionAsync(string environment,string code,string callbackUrl,string? expectedAccountId,CancellationToken ct)
    {
        Require(!string.IsNullOrEmpty(expectedAccountId),"MERCHANT_IDENTITY_MISSING");
        var account=await Api(environment,HttpMethod.Get,"/accounts/"+Escape(expectedAccountId!),null,ct);
        Require(S(account,"id")==expectedAccountId && S(account,"type")=="standard","MERCHANT_IDENTITY_MISMATCH");
        return new(expectedAccountId!,new MerchantCredential(""),["connect:direct_charges"]);
    }
    public async Task<MerchantReadiness> GetReadinessAsync(MerchantAccount account,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/accounts/"+Escape(account.AccountId),null,ct);
        Require(S(result,"id")==account.AccountId && S(result,"type")=="standard","MERCHANT_IDENTITY_MISMATCH");
        var currency=S(result,"default_currency").ToUpperInvariant();var ready=B(result,"charges_enabled");
        return new(ready,B(result,"payouts_enabled"),currency==""?[]:[currency],["card"],[],ready?null:"Complete Stripe account verification.");
    }
    public Task<MerchantCredential> RefreshAsync(MerchantAccount account,CancellationToken ct) => Task.FromResult(account.Credential);
    public Task RevokeAsync(MerchantAccount account,CancellationToken ct) => throw new MerchantPaymentException("PROVIDER_REVOKE_UNSUPPORTED_USE_DASHBOARD");
    public async Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount account,MerchantCheckoutRequest request,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Post,"/checkout/sessions",Form(("mode","payment"),("success_url",request.ReturnUrl),("cancel_url",request.ReturnUrl),("client_reference_id",request.OrderReference),("metadata[attempt_id]",request.AttemptId.ToString("D")),("payment_intent_data[metadata][attempt_id]",request.AttemptId.ToString("D")),("line_items[0][price_data][currency]",request.Currency.ToLowerInvariant()),("line_items[0][price_data][unit_amount]",request.AmountMinor.ToString(CultureInfo.InvariantCulture)),("line_items[0][price_data][product_data][name]","Order "+request.OrderReference),("line_items[0][quantity]","1"),("payment_method_types[0]","card")),ct,account.AccountId,request.IdempotencyKey);
        Require(S(result,"id")!="" && S(result,"url")!="","PROVIDER_INVALID_RESPONSE");
        Require(B(result,"livemode")== (account.Environment=="Production"),"PROVIDER_ENVIRONMENT_MISMATCH");
        return new(account.AccountId,account.Environment,S(result,"id"),S(result,"payment_intent"),"Pending",request.AmountMinor,request.Currency,CheckoutUrl:S(result,"url"));
    }
    public async Task<MerchantPaymentEvidence?> FindCheckoutAsync(MerchantAccount account,MerchantCheckoutRequest request,CancellationToken ct)
    {
        string? cursor=null;
        for(var page=0;page<3;page++)
        {
            var result=await Api(account.Environment,HttpMethod.Get,"/checkout/sessions?limit=100"+(cursor is null?"":"&starting_after="+Escape(cursor)),null,ct,account.AccountId);
            var sessions=P(result,"data").EnumerateArray().ToArray();
            var matches=sessions.Where(x=>S(P(x,"metadata"),"attempt_id")==request.AttemptId.ToString("D") && S(x,"client_reference_id")==request.OrderReference).ToArray();
            Require(matches.Length<=1,"AMBIGUOUS_PROVIDER_CHECKOUT");
            if(matches.Length==1)
            {
                var evidence=await GetPaymentAsync(account,S(matches[0],"id"),ct);
                Require(evidence.AmountMinor==request.AmountMinor && evidence.Currency==request.Currency,"PAYMENT_AMOUNT_MISMATCH");
                return evidence with {CheckoutUrl=S(matches[0],"url")};
            }
            if(!B(result,"has_more") || sessions.Length==0)return null;cursor=S(sessions[^1],"id");
        }
        return null; // Bounded discovery is inconclusive, never permission to charge again.
    }
    public async Task<MerchantPaymentEvidence> GetPaymentAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/checkout/sessions/"+Escape(reference)+"?expand[]=payment_intent.latest_charge",null,ct,account.AccountId);
        Require(S(result,"id")==reference && B(result,"livemode")== (account.Environment=="Production"),"PROVIDER_ENVIRONMENT_MISMATCH");
        var intent=P(result,"payment_intent");var charge=intent.ValueKind==JsonValueKind.Object?P(intent,"latest_charge"):default;
        var captured=S(result,"payment_status")=="paid" && intent.ValueKind==JsonValueKind.Object && S(intent,"status")=="succeeded";
        var refunded=charge.ValueKind==JsonValueKind.Object?L(charge,"amount_refunded"):0;
        return new(account.AccountId,account.Environment,reference,intent.ValueKind==JsonValueKind.Object?S(intent,"id"):null,captured?"Captured":"Pending",L(result,"amount_total"),S(result,"currency").ToUpperInvariant(),refunded,CheckoutUrl:S(result,"url"),DisputeState:charge.ValueKind==JsonValueKind.Object && B(charge,"disputed")?"Disputed":null);
    }
    public async Task<MerchantRefundResult> RefundAsync(MerchantAccount account,MerchantRefundRequest request,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Post,"/refunds",Form(("payment_intent",request.PaymentReference),("amount",request.AmountMinor.ToString(CultureInfo.InvariantCulture))),ct,account.AccountId,request.IdempotencyKey);
        return RefundResult(result);
    }
    private static MerchantRefundResult RefundResult(JsonElement result) => new(S(result,"id"),S(result,"status") switch {"succeeded"=>"Succeeded","pending" or "requires_action"=>"Pending","failed" or "canceled"=>"Failed",_=>"Unknown"},S(result,"payment_intent"),L(result,"amount"),S(result,"currency").ToUpperInvariant());
    public async Task<MerchantRefundResult> GetRefundAsync(MerchantAccount account,string reference,CancellationToken ct)
    {
        var result=await Api(account.Environment,HttpMethod.Get,"/refunds/"+Escape(reference),null,ct,account.AccountId);
        Require(S(result,"id")==reference,"PROVIDER_REFERENCE_MISMATCH");return RefundResult(result);
    }
    public async Task<MerchantWebhook?> VerifyWebhookAsync(string environment,string body,IReadOnlyDictionary<string,string> headers,string endpointUrl,CancellationToken ct)
    {
        if(!PaymentWebhookSignatures.VerifyStripe(body,Header(headers,"Stripe-Signature"),Setting(Provider,environment,"WebhookSecret"),DateTimeOffset.UtcNow))return null;
        using var doc=JsonDocument.Parse(body);var root=doc.RootElement;
        if(B(root,"livemode")!=(environment=="Production"))return null;
        var account=S(root,"account");if(account=="")return null; var obj=P(P(root,"data"),"object");var kind=S(obj,"object"); string reference;
        Guid? attempt=Guid.TryParse(S(P(obj,"metadata"),"attempt_id"),out var parsed)?parsed:null;
        if(kind=="checkout.session")reference=S(obj,"id");
        else
        {
            var intent=kind=="payment_intent"?S(obj,"id"):S(obj,"payment_intent");
            if(intent=="")return null;
            var sessions=await Api(environment,HttpMethod.Get,"/checkout/sessions?payment_intent="+Escape(intent)+"&limit=2",null,ct,account);
            var data=P(sessions,"data").EnumerateArray().ToArray(); if(data.Length!=1)return null;reference=S(data[0],"id");
            attempt=Guid.TryParse(S(P(data[0],"metadata"),"attempt_id"),out var sessionAttempt)?sessionAttempt:null;
        }
        return new(S(root,"id"),account,environment,reference,DateTimeOffset.FromUnixTimeSeconds(L(root,"created")).UtcDateTime,attempt);
    }
}
