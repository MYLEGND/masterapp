using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingClientScriptContractTests
{
    private static string RepoRoot => ResolveRepoRoot();

    [Fact]
    public void ComposerAvailability_UsesSharedActiveConversationState()
    {
        var source = ReadRepositoryFile("SHARED", "wwwroot", "js", "messaging.js");
        var composerBody = GetFunctionBody(source, "setComposerState");

        Assert.Contains("state.active?.id", composerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Boolean(target?.contactKey || conversation)", composerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMessagingClient_UsesOneOfficialSignalRConnection()
    {
        var source = ReadRepositoryFile("SHARED", "wwwroot", "js", "messaging.js");

        Assert.Equal(1, CountOccurrences(source, "new window.signalR.HubConnectionBuilder()"));
        Assert.Equal(1, CountOccurrences(source, ".withAutomaticReconnect()"));
        Assert.Equal(1, CountOccurrences(source, ".build()"));
        Assert.Contains("connection.onreconnecting(startPolling);", source, StringComparison.Ordinal);
        Assert.Contains("connection.onreconnected(stopPolling);", source, StringComparison.Ordinal);
        Assert.Contains("connection.onclose(startPolling);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMessagingClient_UsesFullParticipantIdentityForConversationState()
    {
        var source = ReadRepositoryFile("SHARED", "wwwroot", "js", "messaging.js");

        Assert.Contains("data-current-participant-type", ReadRepositoryFile("SHARED", "Views", "Messaging", "_CommandCenter.cshtml"), StringComparison.Ordinal);
        Assert.Contains("function participantIdentityKey(userId, participantType)", source, StringComparison.Ordinal);
        Assert.Contains("isCurrentParticipant(message.senderUserId, message.senderType)", source, StringComparison.Ordinal);
        Assert.Contains("participantIdentityKey(conversation.counterparty?.userId, conversation.counterparty?.participantType)", source, StringComparison.Ordinal);
        Assert.Contains("participantIdentityKey(state.draftTarget.userId, state.draftTarget.participantType)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AgentPortal", "Views", "Shared", "_Layout.cshtml")]
    [InlineData("AgentPortal", "Views", "Shared", "_ClientWorkspaceLayout.cshtml")]
    [InlineData("ClientApp", "Views", "Shared", "_Layout.cshtml")]
    public void RenderedLayouts_LoadTheSingleSharedMessagingClientExactlyOnce(
        string application,
        string views,
        string shared,
        string fileName)
    {
        var source = ReadRepositoryFile(application, views, shared, fileName);

        Assert.Equal(1, CountOccurrences(source, "~/Views/Messaging/_SignalRClient.cshtml"));
        Assert.Equal(1, CountOccurrences(source, "~/_content/Shared/js/messaging.js"));
        Assert.Equal(1, CountOccurrences(source, "~/_content/Shared/css/dashboard-home-shared.css"));
    }

    private static string GetFunctionBody(string source, string functionName)
    {
        var marker = $"function {functionName}(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate {marker} in shared messaging client.");

        var nextFunction = source.IndexOf("\n  function ", start + marker.Length, StringComparison.Ordinal);
        Assert.True(nextFunction > start, $"Could not locate the end of {functionName} in shared messaging client.");
        return source[start..nextFunction];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. path]));

    private static string ResolveRepoRoot()
    {
        var candidates = new List<string>();
        var currentDirectory = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            candidates.Add(Path.GetFullPath(currentDirectory));
        }

        var sourceDirectory = Path.GetDirectoryName(GetSourceFilePath());
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(sourceDirectory, "..")));
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (baseDirectory is not null)
        {
            candidates.Add(baseDirectory.FullName);
            baseDirectory = baseDirectory.Parent;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, "SHARED", "wwwroot", "js", "messaging.js")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the MASTERAPP repository root containing SHARED/wwwroot/js/messaging.js.");
    }

    private static string GetSourceFilePath([CallerFilePath] string path = "") => path;
}
