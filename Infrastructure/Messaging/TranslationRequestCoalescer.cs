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
        Func<Task<T>> factory);
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
        Func<Task<T>> factory)
    {
        var candidate = new Lazy<Task<object>>(
            async () => (object)(await factory())!,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _inFlight.GetOrAdd(identity, candidate);
        try
        {
            return new CoalescedTranslationResult<T>(
                (T)await selected.Value,
                !ReferenceEquals(selected, candidate));
        }
        finally
        {
            _inFlight.TryRemove(
                new KeyValuePair<string, Lazy<Task<object>>>(identity, selected));
        }
    }
}
