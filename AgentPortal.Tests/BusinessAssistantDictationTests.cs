using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessAssistantDictationTests
{
    private static byte[] Wave(int samples = 1600)
    {
        var result = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(result); "WAVEfmt "u8.CopyTo(result.AsSpan(8)); "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)samples * 2);
        return result;
    }

    [Fact]
    public void DurationIsDerivedFromCanonicalPcmNotClientMetadata()
    {
        LegendFounderAiConversationService.ValidateDictationWave(Wave());
        LegendFounderAiConversationService.ValidateDictationWave(Wave(960000));
        Assert.Throws<ArgumentException>(() => LegendFounderAiConversationService.ValidateDictationWave(Wave(960001)));
        Assert.Throws<ArgumentException>(() => LegendFounderAiConversationService.ValidateDictationWave(Wave(1599)));
        var compressed = Wave(); compressed[20] = 3;
        Assert.Throws<ArgumentException>(() => LegendFounderAiConversationService.ValidateDictationWave(compressed));
        var mismatched = Wave(); mismatched[40] = 0;
        Assert.Throws<ArgumentException>(() => LegendFounderAiConversationService.ValidateDictationWave(mismatched));
        Assert.Throws<ArgumentException>(() => LegendFounderAiConversationService.ValidateDictationWave(new byte[2]));
    }

    [Fact]
    public async Task MalformedDictationIsRejectedBeforeServiceOrProviderAccess()
    {
        var controller = new LegendFounderAiController(null!, new LegendFounderAiProgressBroker(), NullLogger<LegendFounderAiController>.Instance);
        Assert.IsType<BadRequestResult>(await controller.BusinessDictation(null!, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.BusinessDictation(new("", Guid.NewGuid(), Guid.NewGuid(), ""), CancellationToken.None));
    }
}
