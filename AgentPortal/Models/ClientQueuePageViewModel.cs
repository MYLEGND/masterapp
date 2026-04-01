namespace AgentPortal.Models;

public sealed class ClientQueuePageViewModel
{
    public string QueueKey { get; set; } = "";
    public string QueueTitle { get; set; } = "";
    public string QueueDescription { get; set; } = "";
    public string QueueRule { get; set; } = "";
    public List<ClientListItemViewModel> Items { get; set; } = new();
}
