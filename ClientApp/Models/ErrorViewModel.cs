using Shared.Diagnostics;

namespace AgentPortal.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public AppFailureDiagnostics? Diagnostics { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public bool HasDiagnostics => Diagnostics is not null;
}
