namespace AgentPortal.Services;

public class NoOpNotificationService : INotificationService
{
    public Task NotifyAsync(string userId, string subject, string body, CancellationToken ct = default)
    {
        // Intentionally no-op for MVP; replace with email/SMS later.
        return Task.CompletedTask;
    }
}
