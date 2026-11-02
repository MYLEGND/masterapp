using System;
using System.Text.Json;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace AgentPortal.Tests;

public sealed class StoreManagementLoginRequestTests
{
    private readonly EphemeralDataProtectionProvider _provider = new();

    [Theory]
    [InlineData("business")]
    [InlineData("legend")]
    [InlineData("protect")]
    public void Request_round_trip_is_not_an_editor_ticket(string site)
    {
        using var protector = new WebsiteEditorTicketProtector(_provider);
        var request = Valid(site);
        var token = protector.ProtectStoreLoginRequest(request);
        Assert.Equal(request, protector.TryUnprotectStoreLoginRequest(token));
        Assert.Null(protector.TryUnprotect(token));
        var editor = protector.Protect(new WebsiteEditorTicket(site, request.OwnerKey, null, site == "legend",
            DateTime.UtcNow.AddHours(1), request.CommerceBusinessId, "synthetic-actor"));
        Assert.Null(protector.TryUnprotectStoreLoginRequest(editor));
        Assert.Null(protector.TryUnprotectStoreLoginRequest(token[..^5] + "wrong"));
    }

    [Theory]
    [InlineData("http://store.example.invalid")]
    [InlineData("https://store.example.invalid/path")]
    [InlineData("https://user@store.example.invalid")]
    [InlineData("https://store.example.invalid?return=bad")]
    [InlineData("https://store.example.invalid#fragment")]
    [InlineData("https://store.example.invalid:8443")]
    [InlineData("https://store.example.invalid/")]
    [InlineData("https://127.0.0.1")]
    public void Noncanonical_or_insecure_origin_is_rejected(string origin) => Reject(Valid() with { DestinationOrigin = origin });

    [Theory]
    [InlineData("https://elsewhere.example.invalid/internal")]
    [InlineData("//elsewhere.example.invalid/internal")]
    [InlineData("/internality")]
    [InlineData("/internal/../account")]
    [InlineData("/internal/%2e%2e/account")]
    [InlineData("/internal//account")]
    [InlineData("/internal\\account")]
    [InlineData("/internal#fragment")]
    [InlineData("/internal\r\nLocation: elsewhere")]
    public void Return_path_cannot_escape_internal_workspace(string path) => Reject(Valid() with { ReturnPath = path });

    [Theory]
    [InlineData("/internal")]
    [InlineData("/internal/commerce/orders")]
    [InlineData("/internal/settings/payments?businessKey=synthetic-store")]
    public void Local_scoped_destination_is_preserved(string path)
    {
        using var protector = new WebsiteEditorTicketProtector(_provider);
        var request = Valid() with { ReturnPath = path };
        Assert.Equal(path, protector.TryUnprotectStoreLoginRequest(protector.ProtectStoreLoginRequest(request))!.ReturnPath);
    }

    [Fact]
    public void Missing_scope_browser_binding_or_short_lifetime_is_rejected()
    {
        var request = Valid();
        Reject(request with { CommerceBusinessId = Guid.Empty });
        Reject(request with { SiteKey = "other" });
        Reject(request with { OwnerKey = "another-owner" });
        Reject(request with { BrowserCorrelationHash = "" });
        Reject(request with { BrowserCorrelationHash = new string('z', 64) });
        Reject(request with { ExpiresUtc = DateTime.UtcNow.AddSeconds(-1) });
        Reject(request with { ExpiresUtc = DateTime.UtcNow.AddHours(1) });
        Reject(request with { ExpiresUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(1), DateTimeKind.Unspecified) });
        Reject(Valid("legend") with { OwnerKey = "not-the-founder-site" });
    }

    private void Reject(StoreManagementLoginRequest request)
    {
        using var protector = new WebsiteEditorTicketProtector(_provider);
        Assert.Throws<ArgumentException>(() => protector.ProtectStoreLoginRequest(request));
        // Exercise reader validation as well as the issuer, including a once-valid expired request.
        var encoded = _provider.CreateProtector("LEGEND.StoreManagement.LoginRequest.v1").Protect(
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(protector.TryUnprotectStoreLoginRequest(encoded));
    }

    private static StoreManagementLoginRequest Valid(string site = "business")
    {
        var business = Guid.NewGuid();
        return new(business, site, site switch
        {
            "business" => WebsiteEditorSiteKeys.BusinessOwnerKey(business),
            "legend" => WebsiteEditorSiteKeys.GlobalOwnerKey,
            _ => "synthetic-agent"
        }, "https://store.example.invalid", "/internal/dashboard", new string('a', 64), DateTime.UtcNow.AddMinutes(2));
    }
}
