using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiStreamingTests
{
    [Fact]
    public async Task OversizedUnterminatedLineIsRejectedBeforeDispatch()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', 1024 * 1024)));
        var callbacks = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            LegendFounderAiConversationService.ReadResponseStreamAsync(stream, (_, _) =>
            { callbacks++; return ValueTask.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task TextIsDeliveredBeforeTerminalResponseAndIncompleteTransportFails()
    {
        var delivered = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n"));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            LegendFounderAiConversationService.ReadResponseStreamAsync(stream, (text, _) =>
            { delivered.Add(text); return ValueTask.CompletedTask; }, CancellationToken.None));
        Assert.Equal(["Hello"], delivered);
    }

    [Fact]
    public async Task TerminalResponseRetainsUsageAndOutputWithoutExposingReasoning()
    {
        var delivered = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            ": keepalive\n\ndata: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"private\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Answer\"}\r\n\r\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"output_tokens\":1}}}\n\n"));
        using var result = await LegendFounderAiConversationService.ReadResponseStreamAsync(stream, (text, _) =>
        { delivered.Add(text); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(["Answer"], delivered);
        Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task CancellationStopsReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {}\n\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LegendFounderAiConversationService.ReadResponseStreamAsync(stream, (_, _) => ValueTask.CompletedTask, cancellation.Token));
    }
}
