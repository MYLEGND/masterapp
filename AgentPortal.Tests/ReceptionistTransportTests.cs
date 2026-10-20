using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Reception;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistTransportTests
{
    private const string Secret = "fixture-signing-secret-32-characters";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static readonly byte[] Incoming = Encoding.UTF8.GetBytes("""
        {"object":"event","id":"evt_incoming","type":"live.transport.incoming","data":{"type":"sip","session_id":"live_opaque","owner":"different-owner","project_id":"different-project","sip_headers":[{"name":"From","value":"untrusted-caller"}]}}
        """);

    private static string Signature(byte[] body, string timestamp, string delivery = "wh_delivery", string secret = Secret)
    {
        var signed = Encoding.UTF8.GetBytes(delivery + "." + timestamp + ".").Concat(body).ToArray();
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
    }

    [Fact]
    public void SignedIncomingProjectsOnlySessionIdentityAndNeverSipAuthority()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var result = OpenAiReceptionistProtocol.VerifyIncoming(Incoming, "wh_delivery", timestamp,
            Signature(Incoming, timestamp), Secret, Now);
        Assert.Equal(new OpenAiReceptionistIngressResult("incoming", "wh_delivery", "evt_incoming", "live_opaque"), result);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("different-owner", output);
        Assert.DoesNotContain("different-project", output);
        Assert.DoesNotContain("untrusted-caller", output);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void SignedOldOrFutureDeliveryCannotEnterAdmission(int offset)
    {
        var timestamp = Now.AddSeconds(offset).ToUnixTimeSeconds().ToString();
        Assert.Equal("invalid_signature", OpenAiReceptionistProtocol.VerifyIncoming(Incoming,
            "wh_delivery", timestamp, Signature(Incoming, timestamp), Secret, Now).State);
    }

    [Fact]
    public void SignatureBindsExactBodyTimestampAndDeliveryIdentityAndSupportsRotation()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signature = Signature(Incoming, timestamp);
        Assert.Equal("invalid_signature", OpenAiReceptionistProtocol.VerifyIncoming(Incoming.Concat(new byte[] { 32 }).ToArray(),
            "wh_delivery", timestamp, signature, Secret, Now).State);
        Assert.Equal("invalid_signature", OpenAiReceptionistProtocol.VerifyIncoming(Incoming,
            "wh_other", timestamp, signature, Secret, Now).State);
        Assert.Equal("invalid_signature", OpenAiReceptionistProtocol.VerifyIncoming(Incoming,
            "wh_delivery", Now.AddSeconds(1).ToUnixTimeSeconds().ToString(), signature, Secret, Now).State);
        var encoded = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret));
        Assert.Equal("incoming", OpenAiReceptionistProtocol.VerifyIncoming(Incoming, "wh_delivery", timestamp,
            "v1,invalid " + signature, encoded, Now).State);
    }

    [Theory]
    [InlineData("realtime.call.incoming")]
    [InlineData("live.call.incoming")]
    public void OtherApiEventsCannotCreateAnotherAcceptanceOwner(string type)
    {
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Incoming).Replace("live.transport.incoming", type));
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        Assert.Equal("ignored", OpenAiReceptionistProtocol.VerifyIncoming(body, "wh_delivery", timestamp,
            Signature(body, timestamp), Secret, Now).State);
    }

    [Fact]
    public void LiveAcceptPreservesConfiguredPolicyAndVoiceWithoutAnotherModelOrSipAudioFormat()
    {
        const string policy = "Exact configured conversation policy.\nSecond line remains exact.";
        using var body = JsonDocument.Parse(OpenAiReceptionistProtocol.AcceptBody(
            new("gpt-live-1", "marin", policy, "application_services")));
        var session = body.RootElement.GetProperty("session");
        Assert.Equal(policy, session.GetProperty("instructions").GetString());
        Assert.Equal("gpt-live-1", session.GetProperty("model").GetString());
        Assert.Equal("marin", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.False(session.GetProperty("audio").TryGetProperty("format", out _));
        Assert.False(session.GetProperty("store").GetBoolean());
        Assert.Equal("client", session.GetProperty("delegation").GetProperty("type").GetString());
        Assert.False(session.GetProperty("delegation").TryGetProperty("responses", out _));
        Assert.Throws<InvalidOperationException>(() => OpenAiReceptionistProtocol.AcceptBody(new("unapproved", "marin", policy, "application_services")));
        Assert.Throws<InvalidOperationException>(() => OpenAiReceptionistProtocol.AcceptBody(new("gpt-live-1", "marin", policy, "responses")));
        Assert.Throws<InvalidOperationException>(() => OpenAiReceptionistProtocol.AcceptBody(new("gpt-live-1", "", policy, "application_services")));
    }

    [Fact]
    public void SessionControlsStayOnTheVerifiedProviderAndOpaqueIdCannotBecomeAPath()
    {
        Assert.Equal("https://api.openai.com/v1/live/sessions/live_opaque/accept",
            OpenAiReceptionistProtocol.SessionUri("live_opaque", "accept").AbsoluteUri);
        Assert.Equal("wss://api.openai.com/v1/live/sessions/live_opaque/attach",
            OpenAiReceptionistProtocol.SessionUri("live_opaque", "attach", true).AbsoluteUri);
        foreach (var id in new[] { "..", "../other", "live?owner=x", "live#other", "live\r\nheader" })
            Assert.Throws<ArgumentException>(() => OpenAiReceptionistProtocol.SessionUri(id, "accept"));
        Assert.Throws<ArgumentException>(() => OpenAiReceptionistProtocol.SessionUri("live_opaque", ""));
        Assert.Throws<ArgumentException>(() => OpenAiReceptionistProtocol.SessionUri("live_opaque", "content"));
    }

    [Fact]
    public void DurationEventsAreCumulativeAndOnlyClosedIsFinal()
    {
        using var update = JsonDocument.Parse("""{"type":"session.usage.updated","usage":{"seconds":45.8}}""");
        Assert.True(OpenAiReceptionistProtocol.TryCumulativeUsage(update.RootElement, out var seconds, out var final));
        Assert.Equal(45.8m, seconds); Assert.False(final);
        using var closed = JsonDocument.Parse("""{"type":"session.closed","usage":{"seconds":46.1}}""");
        Assert.True(OpenAiReceptionistProtocol.TryCumulativeUsage(closed.RootElement, out seconds, out final));
        Assert.Equal(46.1m, seconds); Assert.True(final);
        using var malformed = JsonDocument.Parse("""{"type":"session.closed","usage":{"seconds":"guess"}}""");
        Assert.False(OpenAiReceptionistProtocol.TryCumulativeUsage(malformed.RootElement, out _, out _));
    }

    [Fact]
    public async Task AcceptanceTimeoutRemainsUnknownAndIsNeverRetriedByTransport()
    {
        var handler = new Handler(_ => throw new HttpRequestException("provider outcome unknown"));
        var client = new OpenAiReceptionistClient(new Factory(handler));
        var result = await client.AcceptAsync("proj_fixture", "live_fixture", "fixture-server-credential",
            new("gpt-live-1", "marin", "Fixture policy", "application_services"), CancellationToken.None);
        Assert.False(result.Confirmed);
        Assert.Null(result.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("openai_http:accept:outcome_unknown", result.Receipt);
    }

    [Fact]
    public async Task ControlsUseExactProjectAndSuccessDoesNotInventBillingOrClosure()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("proj_fixture", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new OpenAiReceptionistClient(new Factory(handler));
        var result = await client.HangupAsync("proj_fixture", "live_fixture", "fixture-server-credential", CancellationToken.None);
        Assert.True(result.Confirmed);
        Assert.Equal("openai_http:hangup:200:request_id_unavailable", result.Receipt);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MultipleRequestIdsCannotTurnAConfirmedProviderCommandIntoAnUnobservedException()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("x-request-id", new[] { "req_first", "req_second" });
            return response;
        });
        var client = new OpenAiReceptionistClient(new Factory(handler));
        var result = await client.HangupAsync("proj_fixture", "live_fixture", "fixture-server-credential", CancellationToken.None);
        Assert.True(result.Confirmed);
        Assert.Equal("openai_http:hangup:200:request_id_unavailable", result.Receipt);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(send(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
