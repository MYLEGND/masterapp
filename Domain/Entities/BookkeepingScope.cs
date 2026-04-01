namespace Domain.Entities;

// ✅ Critical boundary to prevent leakage
// ClientShared = shared between client + their assigned agent
// AgentPrivate = agent-only bookkeeping (never shown to client)
public enum BookkeepingScope
{
    ClientShared = 0,
    AgentPrivate = 1
}
