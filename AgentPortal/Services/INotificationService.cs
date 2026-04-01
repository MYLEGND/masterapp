namespace AgentPortal.Services;

public interface INotificationService
{
    Task NotifyAsync(string userId, string subject, string body, CancellationToken ct = default);
}
