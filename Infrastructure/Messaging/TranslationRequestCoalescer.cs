using System.Collections.Concurrent;
using Domain.Messaging;

namespace Infrastructure.Messaging;

internal sealed record CoalescedTranslationResult<T>(
    T Result,
    bool JoinedExistingRequest);

internal interface ITranslationRequestCoalescer
{
    Task<CoalescedTranslationResult<T>> ExecuteAsync<T>(
        string identity,
        Func<Task<T>> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local request coalescing only. It stores no translation result: the
/// durable authority remains LegendTranslationAlignments, while the existing
/// provider-capacity reservation reference supplies the cross-instance fence.
/// </summary>
internal sealed class TranslationRequestCoalescer : ITranslationRequestCoalescer
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight =
        new(StringComparer.Ordinal);

    public async Task<CoalescedTranslationResult<T>> ExecuteAsync<T>(
        string identity,
        Func<Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<object>> candidate = null!;
        candidate = new Lazy<Task<object>>(async () =>
        {
            try
            {
                return (object)(await factory())!;
            }
            finally
            {
                // Only completion of the shared work releases this fence. A
                // cancelled waiter must not permit a duplicate Azure request.
                _inFlight.TryRemove(
                    new KeyValuePair<string, Lazy<Task<object>>>(identity, candidate));
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _inFlight.GetOrAdd(identity, candidate);
        var joined = !ReferenceEquals(selected, candidate);
        // The owner retains its scoped dependencies until the factory has
        // honored its cancellation token. Other callers can stop waiting
        // independently without cancelling the owner's operation.
        var result = joined
            ? await selected.Value.WaitAsync(cancellationToken)
            : await selected.Value;
        return new CoalescedTranslationResult<T>((T)result, joined);
    }
}
