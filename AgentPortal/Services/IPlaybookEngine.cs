namespace AgentPortal.Services;

public interface IPlaybookEngine
{
    /// <summary>
    /// Handles domain events; implementation must be idempotent.
    /// </summary>
    Task HandleAsync(string eventName, string executionKey, object payload, CancellationToken ct = default);
}
