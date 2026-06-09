using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public class TrackingFailLoudContractTests
{
    [Fact]
    public void TrackingJs_RetainsFailLoudQueueAndFlushGuards()
    {
        var file = Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "tracking.js");
        var content = File.ReadAllText(file);

        Assert.Contains("const TRACKING_MAX_RETRIES = 3;", content, StringComparison.Ordinal);
        Assert.Contains("const TRACKING_QUEUE_LIMIT = 80;", content, StringComparison.Ordinal);
        Assert.Contains("queue.slice(-TRACKING_QUEUE_LIMIT)", content, StringComparison.Ordinal);
        Assert.Contains("const isDiagnosticEvent = body.EventType === CLIENT_TRACKING_ERROR_EVENT;", content, StringComparison.Ordinal);
        Assert.Contains("const response = await nativeFetch(INGEST_URL", content, StringComparison.Ordinal);
        Assert.Contains("queueCriticalEvent(body, lastFailure, attempt, 'send_failed');", content, StringComparison.Ordinal);
        Assert.Contains("void flushQueuedEvents('page_load');", content, StringComparison.Ordinal);
        Assert.Contains("void flushQueuedEvents('visibility_hidden');", content, StringComparison.Ordinal);
        Assert.Contains("void flushQueuedEvents('visibility_visible');", content, StringComparison.Ordinal);
        Assert.Contains("void flushQueuedEvents('pagehide', { useBeacon: true, maxItems: 5 });", content, StringComparison.Ordinal);
        Assert.Contains("void flushQueuedEvents('thank_you_load');", content, StringComparison.Ordinal);
        Assert.Contains("sendEvent({ EventType: 'lead_form_start', FormKey: formKey });", content, StringComparison.Ordinal);
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}
