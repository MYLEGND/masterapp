using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AgentPortal.Services;

/// <summary>
/// Ephemeral transport-only delivery for Founder AI progress events.
/// This broker is not a knowledge store, authority, or durable event log.
/// </summary>
public sealed class LegendFounderAiProgressBroker
{
    private const int Capacity = 8;

    private readonly ConcurrentDictionary<Guid, OperationProgress> _operations = new();

    public ChannelReader<LegendFounderAiProgressEvent> Subscribe(Guid operationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);

        return _operations
            .GetOrAdd(operationId, static _ => new OperationProgress())
            .Channel
            .Reader;
    }

    public ValueTask PublishAsync(
        Guid operationId,
        LegendFounderAiProgressEvent update,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(update);

        cancellationToken.ThrowIfCancellationRequested();

        var operation = _operations.GetOrAdd(
            operationId,
            static _ => new OperationProgress());

        // Progress is advisory and ephemeral. Never allow a slow or abandoned
        // subscriber to create unbounded memory growth or backpressure the
        // authoritative chat operation. DropOldest preserves the freshest
        // bounded progress window.
        operation.Channel.Writer.TryWrite(update);

        return ValueTask.CompletedTask;
    }

    public void Complete(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return;
        }

        if (_operations.TryRemove(operationId, out var operation))
        {
            operation.Channel.Writer.TryComplete();
        }
    }

    internal int ActiveOperationCount => _operations.Count;

    private sealed class OperationProgress
    {
        public Channel<LegendFounderAiProgressEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<LegendFounderAiProgressEvent>(
                new BoundedChannelOptions(Capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });
    }
}
