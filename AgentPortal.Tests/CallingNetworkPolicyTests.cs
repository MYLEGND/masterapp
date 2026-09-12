using System;
using System.Collections.Generic;
using System.Text.Json;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CallingNetworkPolicyTests
{
    [Fact]
    public void SharedPolicy_ProvidesConservativeMeasuredNetworkThresholds()
    {
        var policy = MessagingService.BuildCallPolicy(null);
        Assert.Null(policy.Relay);
        var tuning = Assert.IsType<Shared.Calling.LegendCallAdaptationPolicy>(policy.Adaptation);
        Assert.True(tuning.LowBitrate + policy.AudioBitrate < tuning.LowBandwidth);
        Assert.True(tuning.MediumBitrate + policy.AudioBitrate < tuning.HighBandwidth);
        Assert.True(tuning.RecoverySamples > 1);
        Assert.True(tuning.LowWidth < tuning.MediumWidth && tuning.MediumWidth < policy.WifiWidth);
    }

    [Fact]
    public void RelayCredentials_AreExpiringUniqueAndDoNotExposeSigningSecret()
    {
        var secret = new string('x', 40);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Calling:Relay:Urls:0"] = "turns:relay.example.invalid:443?transport=tcp",
            ["Calling:Relay:SharedSecret"] = secret }).Build();
        var first = MessagingService.BuildCallPolicy(config).Relay!;
        var second = MessagingService.BuildCallPolicy(config).Relay!;
        Assert.NotEqual(first.Username, second.Username);
        Assert.NotEqual(first.Credential, second.Credential);
        Assert.InRange(first.ExpiresUtc, DateTime.UtcNow.AddHours(11), DateTime.UtcNow.AddHours(13));
        Assert.Equal(new DateTimeOffset(first.ExpiresUtc).ToUnixTimeSeconds().ToString(), first.Username.Split(':')[0]);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(first));
        Assert.Equal(20, Convert.FromBase64String(first.Credential).Length);
    }

    [Theory]
    [InlineData("turn:", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("turns:?transport=tcp", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("https://relay.example.invalid", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("turns:relay.example.invalid:443", "")]
    [InlineData(null, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void PartialOrInvalidRelayConfiguration_IsRejected(string? url, string secret)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Calling:Relay:Urls:0"] = url, ["Calling:Relay:SharedSecret"] = secret }).Build();
        Assert.Throws<InvalidOperationException>(() => MessagingService.BuildCallPolicy(config));
    }
}
