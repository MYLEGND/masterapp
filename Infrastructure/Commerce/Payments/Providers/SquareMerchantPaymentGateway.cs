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
    public async Task<MerchantReadiness> GetReadinessAsync(MerchantAccount account, CancellationToken ct)
    {
        var merchant = await Api(account.Environment,HttpMethod.Get,"/v2/merchants/"+Escape(account.AccountId),account.Credential.AccessToken,null,ct);
        Require(S(P(merchant,"merchant"),"id") == account.AccountId,"MERCHANT_IDENTITY_MISMATCH");
        var locations = await Api(account.Environment,HttpMethod.Get,"/v2/locations",account.Credential.AccessToken,null,ct);
        var eligible = P(locations,"locations").EnumerateArray().Where(x => S(x,"merchant_id") == account.AccountId && S(x,"status") == "ACTIVE" && x.TryGetProperty("capabilities",out var caps) && caps.EnumerateArray().Any(c => c.GetString() == "CREDIT_CARD_PROCESSING")).ToArray();
        var selected = eligible.FirstOrDefault(x => S(x,"id") == account.LocationId);
        var ready = selected.ValueKind != JsonValueKind.Undefined && S(P(merchant,"merchant"),"status") == "ACTIVE";
        return new(ready,ready,ready?[S(selected,"currency")]:eligible.Select(x=>S(x,"currency")).Distinct().ToArray(),["card"],eligible.Select(x=>S(x,"id")).ToArray(),ready ? null : "Select an active card-processing location.");
    }
    public async Task RevokeAsync(MerchantAccount account,CancellationToken ct) => await SendAsync(HttpMethod.Post,Base(account.Environment)+"/oauth2/revoke","",Json(new {client_id=Setting(Provider,account.Environment,"ApplicationId"),access_token=account.Credential.AccessToken,revoke_only_access_token=false}),ct,("Authorization","Client "+Setting(Provider,account.Environment,"ApplicationSecret")));
    public async Task<MerchantPaymentEvidence> CreateCheckoutAsync(MerchantAccount account, MerchantCheckoutRequest request,CancellationToken ct)
    {
        Require(!string.IsNullOrWhiteSpace(account.LocationId),"LOCATION_REQUIRED");
        var result = await Api(account.Environment,HttpMethod.Post,"/v2/online-checkout/payment-links",account.Credential.AccessToken,Json(new {idempotency_key=request.IdempotencyKey,order=new {location_id=account.LocationId,reference_id=request.OrderReference,metadata=new {attempt_id=request.AttemptId.ToString("D")},line_items=new[]{new {name="Order "+request.OrderReference,quantity="1",base_price_money=new {amount=request.AmountMinor,currency=request.Currency}}}},checkout_options=new {redirect_url=request.ReturnUrl}}),ct);
        var link=P(result,"payment_link"); Require(S(link,"order_id") != "" && S(link,"url") != "","PROVIDER_INVALID_RESPONSE");
        return new(account.AccountId,account.Environment,S(link,"order_id"),null,"Pending",request.AmountMinor,request.Currency,CheckoutUrl:S(link,"url"));
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
        var reference=payment.ValueKind==JsonValueKind.Object?S(payment,"order_id"):S(P(obj,"refund"),"order_id");
        if(reference=="")return null;
        await Task.CompletedTask;
        return new(S(root,"event_id"),S(root,"merchant_id"),environment,reference,DateTime.Parse(S(root,"created_at")).ToUniversalTime());
    }
}
