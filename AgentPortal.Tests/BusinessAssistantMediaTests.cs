using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAssistantMediaTests
{
    [Fact]
    public async Task ReferenceAndProviderBodiesCannotExceedTheirBound()
    {
        using var source = new MemoryStream(new byte[8193]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LegendFounderAiConversationService.ReadBoundedMediaAsync(source, 8192, CancellationToken.None));
        using var exact = new MemoryStream(new byte[8192]);
        Assert.Equal(8192, (await LegendFounderAiConversationService.ReadBoundedMediaAsync(exact, 8192, CancellationToken.None)).Length);
    }
}
