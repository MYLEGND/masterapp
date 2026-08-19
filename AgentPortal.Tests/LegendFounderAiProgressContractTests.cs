using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Mobile;
using AgentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiProgressContractTests
{
    [Fact]
    public void WebProgress_IsSeparateGetRoute()
    {
        var method = typeof(LegendFounderAiController).GetMethod(nameof(LegendFounderAiController.Progress));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("founder/legend-ai/progress/{operationId:guid}", method.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void WebChat_RemainsStatusBearingPost()
    {
        var method = typeof(LegendFounderAiController).GetMethod(nameof(LegendFounderAiController.Chat));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal(typeof(Task<IActionResult>), method.ReturnType);
    }

    [Fact]
    public void MobileProgress_IsSeparateGetRoute()
    {
        var method = typeof(MobileFounderAiController).GetMethod(nameof(MobileFounderAiController.Progress));
        Assert.NotNull(method);
        Assert.Equal("progress/{operationId:guid}", method!.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    [Fact]
    public async Task Broker_PublishesAndCompletes()
    {
        var broker = new LegendFounderAiProgressBroker();
        var id = Guid.NewGuid();
        var reader = broker.Subscribe(id);
        var expected = new LegendFounderAiProgressEvent("planning", "Planning governed checks.", 1);
        await broker.PublishAsync(id, expected);
        Assert.True(await reader.WaitToReadAsync());
        Assert.True(reader.TryRead(out var actual));
        Assert.Equal(expected, actual);
        broker.Complete(id);
        Assert.False(await reader.WaitToReadAsync());
    }

    [Fact]
    public async Task Broker_CompletionRemovesOperationAndCompletesReader()
    {
        var broker = new LegendFounderAiProgressBroker();
        var operationId = Guid.NewGuid();

        var reader = broker.Subscribe(operationId);

        await broker.PublishAsync(
            operationId,
            new LegendFounderAiProgressEvent(
                "planning",
                "Planning governed checks.",
                1));

        Assert.Equal(1, broker.ActiveOperationCount);

        broker.Complete(operationId);

        Assert.Equal(0, broker.ActiveOperationCount);

        var update = await reader.ReadAsync();

        Assert.Equal("planning", update.Stage);
        Assert.False(await reader.WaitToReadAsync());
    }

    [Fact]
    public async Task Broker_IsolatesConcurrentOperationIds()
    {
        var broker = new LegendFounderAiProgressBroker();

        var firstOperationId = Guid.NewGuid();
        var secondOperationId = Guid.NewGuid();

        var firstReader = broker.Subscribe(firstOperationId);
        var secondReader = broker.Subscribe(secondOperationId);

        var first = new LegendFounderAiProgressEvent(
            "first",
            "First operation.",
            1);

        var second = new LegendFounderAiProgressEvent(
            "second",
            "Second operation.",
            1);

        await broker.PublishAsync(firstOperationId, first);
        await broker.PublishAsync(secondOperationId, second);

        Assert.Equal(first, await firstReader.ReadAsync());
        Assert.Equal(second, await secondReader.ReadAsync());

        Assert.False(firstReader.TryRead(out _));
        Assert.False(secondReader.TryRead(out _));

        broker.Complete(firstOperationId);
        broker.Complete(secondOperationId);

        Assert.Equal(0, broker.ActiveOperationCount);
    }

    [Fact]
    public async Task Broker_BoundsSlowSubscriberAndRetainsNewestProgress()
    {
        var broker = new LegendFounderAiProgressBroker();
        var operationId = Guid.NewGuid();

        var reader = broker.Subscribe(operationId);

        for (var sequence = 1; sequence <= 32; sequence++)
        {
            await broker.PublishAsync(
                operationId,
                new LegendFounderAiProgressEvent(
                    "working",
                    $"Progress {sequence}.",
                    sequence));
        }

        broker.Complete(operationId);

        var received = new List<LegendFounderAiProgressEvent>();

        await foreach (var update in reader.ReadAllAsync())
        {
            received.Add(update);
        }

        Assert.Equal(8, received.Count);

        Assert.Equal(
            Enumerable.Range(25, 8),
            received.Select(update => update.Round!.Value));

        Assert.Equal(0, broker.ActiveOperationCount);
    }

    [Fact]
    public async Task Broker_CompletionOfOneOperationDoesNotCompleteAnother()
    {
        var broker = new LegendFounderAiProgressBroker();

        var completedOperationId = Guid.NewGuid();
        var activeOperationId = Guid.NewGuid();

        var completedReader = broker.Subscribe(completedOperationId);
        var activeReader = broker.Subscribe(activeOperationId);

        broker.Complete(completedOperationId);

        Assert.False(await completedReader.WaitToReadAsync());

        var expected = new LegendFounderAiProgressEvent(
            "working",
            "Still working.",
            2);

        await broker.PublishAsync(activeOperationId, expected);

        Assert.Equal(expected, await activeReader.ReadAsync());
        Assert.Equal(1, broker.ActiveOperationCount);

        broker.Complete(activeOperationId);

        Assert.Equal(0, broker.ActiveOperationCount);
    }

    [Fact]
    public async Task Broker_PublishHonorsCancellationWithoutCreatingOperation()
    {
        var broker = new LegendFounderAiProgressBroker();
        var operationId = Guid.NewGuid();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await broker.PublishAsync(
                    operationId,
                    new LegendFounderAiProgressEvent(
                        "working",
                        "Should not publish.",
                        1),
                    cancellation.Token);
            });

        Assert.Equal(0, broker.ActiveOperationCount);
    }

}
