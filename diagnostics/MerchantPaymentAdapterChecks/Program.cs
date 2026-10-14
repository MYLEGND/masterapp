using System.Net;
using System.Security.Cryptography;
using System.Text;
using Infrastructure.Commerce.Payments;
using Infrastructure.Commerce.Payments.Providers;
using Microsoft.Extensions.Configuration;

var checkedCount=0;
void Check(bool condition,string name) { if(!condition)throw new Exception(name);checkedCount++; }
var key="synthetic-test-key";var body="{\"test\":true}";var endpoint="https://example.invalid/webhook";
var squareSig=Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(endpoint+body)));
Check(PaymentWebhookSignatures.VerifySquare(body,squareSig,key,endpoint),"Square valid signature");
Check(!PaymentWebhookSignatures.VerifySquare(body+" ",squareSig,key,endpoint),"Square changed body");
Check(!PaymentWebhookSignatures.VerifySquare(body,squareSig,key,endpoint+"/other"),"Square changed endpoint");
var now=DateTimeOffset.UtcNow;var stamp=now.ToUnixTimeSeconds();var stripeSig=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(stamp+"."+body)));
Check(PaymentWebhookSignatures.VerifyStripe(body,$"t={stamp},v1=bad,v1={stripeSig}",key,now),"Stripe rotated secret");
Check(!PaymentWebhookSignatures.VerifyStripe(body,$"t={stamp},v1={stripeSig}",key,now.AddMinutes(6)),"Stripe replay");
Check(!PaymentWebhookSignatures.VerifyStripe(body+" ",$"t={stamp},v1={stripeSig}",key,now),"Stripe changed body");
Check(!PaymentWebhookSignatures.VerifyStripe(body,$"t={stamp},t={stamp},v1={stripeSig}",key,now),"Stripe ambiguous timestamp");
var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
 ["MerchantPayments:Stripe:Sandbox:Enabled"]="true",["MerchantPayments:Stripe:Sandbox:SecretKey"]="sk_test_synthetic",
 ["MerchantPayments:Square:Sandbox:Enabled"]="true",["MerchantPayments:Square:Sandbox:ApplicationId"]="synthetic",["MerchantPayments:Square:Sandbox:ApplicationSecret"]="synthetic",
 ["MerchantPayments:PayPal:Production:Enabled"]="true",["MerchantPayments:PayPal:Production:ClientId"]="synthetic",["MerchantPayments:PayPal:Production:ClientSecret"]="synthetic",["MerchantPayments:PayPal:Production:PartnerMerchantId"]="synthetic",["MerchantPayments:PayPal:Production:PartnerAttributionId"]="synthetic"
}).Build();
var factory=new TestFactory();var stripe=new StripeMerchantPaymentGateway(factory,configuration);var square=new SquareMerchantPaymentGateway(factory,configuration);var paypal=new PayPalMerchantPaymentGateway(factory,configuration);
configuration["MerchantPayments:Square:Sandbox:WebhookSignatureKey"]=key;
factory.Handler.Replies.Enqueue("""{"merchant":{"id":"merchant-a","status":"ACTIVE"}}""");
factory.Handler.Replies.Enqueue("""{"locations":[{"id":"location-a","merchant_id":"merchant-a","status":"ACTIVE","currency":"USD","capabilities":["CREDIT_CARD_PROCESSING"]}]}""");
var cardOnly=await square.GetReadinessAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic")),default);
Check(cardOnly.PaymentsEnabled == true && (bool?)cardOnly.SettlementsEnabled is null,"Card processing does not prove settlement readiness");
foreach (var (caps, payments, transfers) in new (string Caps, bool? Payments, bool? Transfers)[]
{
    ("[\"CREDIT_CARD_PROCESSING\"]", true, false),
    ("[\"CREDIT_CARD_PROCESSING\",\"AUTOMATIC_TRANSFERS\"]", true, true),
    ("[\"AUTOMATIC_TRANSFERS\"]", false, true), ("[]", false, false),
    ("null", null, null), ("{}", null, null), ("[42]", null, null),
    ("[\"CREDIT_CARD_PROCESSING\",null]", null, null)
})
{
    factory.Handler.Replies.Enqueue("""{"merchant":{"id":"merchant-a","status":"ACTIVE"}}""");
    factory.Handler.Replies.Enqueue("{\"locations\":[{\"id\":\"location-a\",\"merchant_id\":\"merchant-a\",\"status\":\"ACTIVE\",\"currency\":\"USD\",\"capabilities\":"+caps+"}]}");
    var observed=await square.GetReadinessAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic")),default);
    Check(observed.PaymentsEnabled==payments && observed.AutomaticTransfersEnabled==transfers,"Independent Square capability matrix: "+caps);
    Check(observed.SettlementsEnabled is null && observed.CanAcceptPayments("Square")== (payments==true),"Capabilities never fabricate settlement or require automatic transfers: "+caps);
}
foreach(var payload in new[]{"{}", "{\"locations\":null}", "{\"locations\":{}}",
    """{"locations":[{"id":"location-a","merchant_id":"merchant-a","status":"ACTIVE","currency":"USD"}]}""",
    """{"locations":[{"id":"location-a","merchant_id":"merchant-a","status":42,"currency":"USD","capabilities":["CREDIT_CARD_PROCESSING"]}]}"""})
{
    factory.Handler.Replies.Enqueue("""{"merchant":{"id":"merchant-a","status":"ACTIVE"}}""");
    factory.Handler.Replies.Enqueue(payload);
    var unknown=await square.GetReadinessAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic")),default);
    Check(unknown.PaymentsEnabled is null && unknown.SettlementsEnabled is null && !unknown.CanAcceptPayments("Square"),"Malformed or missing readiness is unknown and blocks checkout");
}
foreach(var identity in new[]{"merchant", "location"})
{
    factory.Handler.Replies.Enqueue(identity=="merchant" ? """{"merchant":{"id":"other","status":"ACTIVE"}}""" : """{"merchant":{"id":"merchant-a","status":"ACTIVE"}}""");
    if(identity=="location")factory.Handler.Replies.Enqueue("""{"locations":[{"id":"location-a","merchant_id":"other","status":"ACTIVE","currency":"USD","capabilities":["CREDIT_CARD_PROCESSING"]}]}""");
    try { await square.GetReadinessAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic")),default); throw new Exception("forged readiness accepted"); }
    catch(MerchantPaymentException ex){Check(ex.Message=="MERCHANT_IDENTITY_MISMATCH","Readiness identity isolation: "+identity);}
}
Check(!new MerchantReadiness(true,null,["USD"],["card"],[]).CanAcceptPayments("Stripe"),"Other provider settlement gate retained");
Check(new MerchantReadiness(true,true,["USD"],["card"],[]).CanAcceptPayments("Stripe"),"Other provider ready remains eligible");
var refundEvent="""
{"event_id":"synthetic-refund-event","merchant_id":"merchant-a","created_at":"2026-10-11T00:00:00Z","data":{"object":{"refund":{"order_id":"distinct-refund-order","payment_id":"original-payment"}}}}
""";
string SignSquare(string payload) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(endpoint+payload)));
var refundHook=await square.VerifyWebhookAsync("Sandbox",refundEvent,new Dictionary<string,string>{{"x-square-hmacsha256-signature",SignSquare(refundEvent)}},endpoint,default);
Check(refundHook?.Reference=="original-payment" && refundHook.AccountId=="merchant-a","Square refund binds original payment rather than refund order");
var unlinkedRefundEvent=refundEvent.Replace(",\"payment_id\":\"original-payment\"","");
Check(await square.VerifyWebhookAsync("Sandbox",unlinkedRefundEvent,new Dictionary<string,string>{{"x-square-hmacsha256-signature",SignSquare(unlinkedRefundEvent)}},endpoint,default) is null,"Square unlinked refund cannot match an unrelated order");
Check(await square.VerifyWebhookAsync("Sandbox",refundEvent,new Dictionary<string,string>{{"x-square-hmacsha256-signature","invalid"}},endpoint,default) is null,"Square refund requires authentic signature");
Check(!stripe.IsAvailable("Production"),"Stripe live unavailable without live config");Check(!paypal.IsAvailable("Production"),"PayPal partner gate");
var account=new MerchantAccount("Stripe","Sandbox","acct_merchant_a",null,new(""));
factory.Handler.Next="{\"id\":\"cs_synthetic\",\"url\":\"https://checkout.stripe.com/synthetic\",\"livemode\":false}";
var result=await stripe.CreateCheckoutAsync(account,new(Guid.NewGuid(),"original-attempt",1000,"USD","order-test","https://example.invalid/return"),default);
Check(result.State=="Pending","Redirect never proves captured");
Check(factory.Handler.Last!.Headers.GetValues("Stripe-Account").Single()=="acct_merchant_a","Stripe seller routing");
Check(factory.Handler.Last.Headers.GetValues("Idempotency-Key").Single()=="original-attempt","Stripe attempt key");
Check(!factory.Handler.Body.Contains("transfer_data") && !factory.Handler.Body.Contains("application_fee"),"No platform custody or fees");
factory.Handler.Next="{\"id\":\"cs_synthetic\",\"url\":\"https://checkout.stripe.com/synthetic\",\"livemode\":true}";
try{await stripe.CreateCheckoutAsync(account,new(Guid.NewGuid(),"original-attempt",1000,"USD","order-test","https://example.invalid/return"),default);throw new Exception("accepted wrong environment");}catch(MerchantPaymentException ex){Check(ex.Message=="PROVIDER_ENVIRONMENT_MISMATCH","Stripe environment proof");}
factory.Handler.Next="{\"order\":{\"id\":\"order-test\",\"location_id\":\"other-location\"}}";
try{await square.GetPaymentAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic-seller-token")),"order-test",default);throw new Exception("accepted wrong location");}catch(MerchantPaymentException ex){Check(ex.Message=="MERCHANT_IDENTITY_MISMATCH","Square location isolation");}
factory.Handler.ThrowTimeout=true;
try{await stripe.CreateCheckoutAsync(account,new(Guid.NewGuid(),"original-attempt",1000,"USD","order-test","https://example.invalid/return"),default);throw new Exception("timeout ignored");}catch(MerchantPaymentException ex){Check(ex.Message=="PROVIDER_OUTCOME_UNKNOWN","Unknown timeout retained");}
factory.Handler.ThrowTimeout=false;
factory.Handler.Next="{\"id\":\"cs_synthetic\",\"url\":\"https://checkout.stripe.com/synthetic\",\"livemode\":false}";
await stripe.CreateCheckoutAsync(account with {AccountId="acct_merchant_b"},new(Guid.NewGuid(),"attempt-b",1000,"USD","order-b","https://example.invalid/return"),default);
Check(factory.Handler.Last!.Headers.GetValues("Stripe-Account").Single()=="acct_merchant_b","Two merchant accounts never share account header");
factory.Handler.Next="{\"data\":[],\"has_more\":false}";
Check(await stripe.FindCheckoutAsync(account,new(Guid.NewGuid(),"attempt-unknown",1000,"USD","order-unknown","https://example.invalid/return"),default) is null,"Unknown discovery inconclusive");
Check(factory.Handler.Last!.Method==HttpMethod.Get,"Unknown reconciliation is read only");
configuration["MerchantPayments:PayPal:Sandbox:Enabled"]="true";
foreach(var setting in new[]{"ClientId","ClientSecret","PartnerMerchantId","PartnerAttributionId"})configuration["MerchantPayments:PayPal:Sandbox:"+setting]="synthetic";
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"tracking_id\":\"server-tracking\",\"merchant_id\":\"provider-verified-account\"}");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"merchant_id\":\"provider-verified-account\",\"oauth_integrations\":[]}");
var connected=await paypal.CompleteConnectionAsync("Sandbox","browser-forged-account","https://example.invalid/callback","server-tracking",default);
Check(connected.AccountId=="provider-verified-account","PayPal callback account comes from provider tracking");
Check(factory.Handler.Last!.RequestUri!.AbsolutePath.EndsWith("/merchant-integrations/provider-verified-account"),"PayPal provider identity revalidated");
factory.Handler.Next="{\"id\":\"re_test\",\"status\":\"succeeded\",\"payment_intent\":\"pi_original\",\"amount\":250,\"currency\":\"usd\"}";
var refund=await stripe.GetRefundAsync(account,"re_test",default);
Check(refund.State=="Succeeded" && refund.PaymentReference=="pi_original" && refund.AmountMinor==250 && refund.Currency=="USD","Stripe refund proof normalized");
Check(factory.Handler.Last!.Headers.GetValues("Stripe-Account").Single()=="acct_merchant_a","Refund stays original merchant");
factory.Handler.Next="{\"refund\":{\"id\":\"refund_square\",\"status\":\"PENDING\",\"payment_id\":\"payment_original\",\"amount_money\":{\"amount\":250,\"currency\":\"USD\"}}}";
var squareRefund=await square.GetRefundAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic-seller-token")),"refund_square",default);
Check(squareRefund.State=="Pending" && squareRefund.PaymentReference=="payment_original" && squareRefund.Currency=="USD","Square refund pending remains pending");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"id\":\"refund_paypal\",\"status\":\"FAILED\",\"amount\":{\"currency_code\":\"USD\",\"value\":\"2.50\"},\"links\":[{\"rel\":\"up\",\"href\":\"https://api-m.sandbox.paypal.com/v2/payments/captures/capture_original\"}]}");
var paypalRefund=await paypal.GetRefundAsync(new("PayPal","Sandbox","merchant-paypal",null,new("")),"refund_paypal",default);
Check(paypalRefund.State=="Failed" && paypalRefund.PaymentReference=="capture_original" && paypalRefund.AmountMinor==250 && paypalRefund.Currency=="USD","PayPal failed refund does not become paid");
Check(factory.Handler.Last!.Headers.Contains("PayPal-Auth-Assertion"),"PayPal refund scoped assertion");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"merchant_id\":\"merchant-paypal\",\"payments_receivable\":true,\"primary_email_confirmed\":true,\"oauth_integrations\":[{\"integration_type\":\"OAUTH_THIRD_PARTY\",\"oauth_third_party\":[{\"partner_client_id\":\"synthetic\",\"scopes\":[\"https://uri.paypal.com/services/payments/payment/authcapture\"]}]}]}");
var paypalReady=await paypal.GetReadinessAsync(new("PayPal","Sandbox","merchant-paypal",null,new("")),default);
Check(paypalReady.PaymentsEnabled == true && paypalReady.Currencies.Contains("USD"),"PayPal documented readiness works without optional currency field");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"merchant_id\":\"merchant-paypal\",\"payments_receivable\":true,\"primary_email_confirmed\":true,\"oauth_integrations\":[{\"oauth_third_party\":[{\"partner_client_id\":\"unrelated-partner\",\"scopes\":[\"https://uri.paypal.com/services/payments/payment/authcapture\"]}]}]}");
Check((await paypal.GetReadinessAsync(new("PayPal","Sandbox","merchant-paypal",null,new("")),default)).PaymentsEnabled != true,"PayPal unrelated partner permission rejected");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"id\":\"refund_readback\",\"status\":\"COMPLETED\"}");
factory.Handler.Replies.Enqueue("{\"access_token\":\"synthetic-token\"}");
factory.Handler.Replies.Enqueue("{\"id\":\"refund_readback\",\"status\":\"COMPLETED\",\"amount\":{\"currency_code\":\"USD\",\"value\":\"2.50\"},\"links\":[{\"rel\":\"up\",\"href\":\"https://api-m.sandbox.paypal.com/v2/payments/captures/capture_original\"}]}");
factory.Handler.CaptureRefundPosts=0;
var readbackRefund=await paypal.RefundAsync(new("PayPal","Sandbox","merchant-paypal",null,new("")),new("capture_original",250,"USD","refund_original_attempt"),default);
Check(readbackRefund.State=="Succeeded" && readbackRefund.AmountMinor==250 && readbackRefund.Currency=="USD" && readbackRefund.PaymentReference=="capture_original","PayPal minimal refund response gets exact authoritative readback");
Check(factory.Handler.CaptureRefundPosts==1 && factory.Handler.RefundRepresentationRequested,"PayPal requests full representation and never reposts refund");
configuration["MerchantPayments:Stripe:Production:Enabled"]="true";
configuration["MerchantPayments:Stripe:Production:SecretKey"]="sk_live_synthetic";
Check(!stripe.IsAvailable("Production"),"Stripe production gated without webhook verification");
configuration["MerchantPayments:Stripe:Production:WebhookSecret"]="synthetic";
configuration["MerchantPayments:Stripe:Production:WebhookUrl"]="https://example.invalid/webhook";
Check(stripe.IsAvailable("Production"),"Stripe configured production option exposed");
configuration["MerchantPayments:Square:Production:Enabled"]="true";
configuration["MerchantPayments:Square:Production:ApplicationId"]="synthetic";
configuration["MerchantPayments:Square:Production:ApplicationSecret"]="synthetic";
Check(!square.IsAvailable("Production"),"Square production gated without webhook verification");
configuration["MerchantPayments:PayPal:Production:PartnerApproved"]="true";
Check(!paypal.IsAvailable("Production"),"PayPal approved partner still needs webhook verification");
factory.Handler.Next="{\"id\":\"cs_synthetic\",\"url\":\"https://checkout.stripe.com/recovered\",\"livemode\":false,\"payment_status\":\"unpaid\",\"amount_total\":1000,\"currency\":\"usd\"}";
Check((await stripe.GetPaymentAsync(account,"cs_synthetic",default)).CheckoutUrl=="https://checkout.stripe.com/recovered","Stripe pending checkout URL recoverable");
var squareAttempt=Guid.NewGuid();
factory.Handler.Replies.Enqueue("{\"orders\":[{\"id\":\"order-recovered\",\"reference_id\":\"reference-recovered\",\"metadata\":{\"attempt_id\":\""+squareAttempt.ToString("D")+"\"}}]}");
factory.Handler.Replies.Enqueue("{\"order\":{\"id\":\"order-recovered\",\"location_id\":\"location-a\",\"total_money\":{\"amount\":1000,\"currency\":\"USD\"}}}");
factory.Handler.Replies.Enqueue("{\"payment_links\":[{\"order_id\":\"unrelated-order\",\"url\":\"https://square.link/other\"},{\"order_id\":\"order-recovered\",\"url\":\"https://square.link/recovered\"}]}");
var recovered=await square.FindCheckoutAsync(new("Square","Sandbox","merchant-a","location-a",new("synthetic-seller-token")),new(squareAttempt,"original-key",1000,"USD","reference-recovered",""),default);
Check(recovered?.State=="Pending" && recovered.CheckoutUrl=="https://square.link/recovered","Square finds exact original hosted link");
Check(factory.Handler.Last!.Method==HttpMethod.Get && factory.Handler.Last.RequestUri!.AbsolutePath=="/v2/online-checkout/payment-links","Square checkout discovery never recreates payment link");
// Future providers stay unavailable until explicitly enabled, even with credentials present.
configuration["MerchantPayments:Stripe:Production:Enabled"]="false";
Check(!stripe.IsAvailable("Production"),"Stripe coming soon cannot connect with credentials alone");
configuration["MerchantPayments:PayPal:Production:WebhookId"]="synthetic";
configuration["MerchantPayments:PayPal:Production:WebhookUrl"]="https://example.invalid/webhook";
configuration["MerchantPayments:PayPal:Production:Enabled"]="false";
Check(!paypal.IsAvailable("Production"),"PayPal coming soon cannot connect with credentials and approval alone");
configuration["MerchantPayments:Square:Production:WebhookSignatureKey"]="synthetic";
configuration["MerchantPayments:Square:Production:WebhookUrl"]="https://example.invalid/webhook";
Check(square.IsAvailable("Production"),"Square can launch while future providers remain disabled");
configuration["MerchantPayments:Stripe:Production:Enabled"]="true";
configuration["MerchantPayments:PayPal:Production:Enabled"]="true";
Check(stripe.IsAvailable("Production") && paypal.IsAvailable("Production"),"Future providers can activate through configuration without code changes");
Console.WriteLine($"PASS {checkedCount} focused adapter checks; synthetic in-memory HTTP only.");
sealed class TestFactory:IHttpClientFactory { public TestHandler Handler {get;}=new();public HttpClient CreateClient(string name)=>new(Handler,false); }
sealed class TestHandler:HttpMessageHandler
{
 public int CaptureRefundPosts;public bool RefundRepresentationRequested;public Queue<string> Replies {get;}=new();public string Next="{}";public string Body="";public HttpRequestMessage? Last;public bool ThrowTimeout;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Last=request;if(request.Method==HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/refund")){CaptureRefundPosts++;RefundRepresentationRequested=request.Headers.TryGetValues("Prefer",out var prefer) && prefer.Contains("return=representation");}Body=request.Content is null?"":await request.Content.ReadAsStringAsync(ct);if(ThrowTimeout)throw new TaskCanceledException();return new(HttpStatusCode.OK){Content=new StringContent(Replies.Count>0?Replies.Dequeue():Next,Encoding.UTF8,"application/json")};}
}
