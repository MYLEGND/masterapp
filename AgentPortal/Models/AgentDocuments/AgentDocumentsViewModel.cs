using System;
using System.Collections.Generic;

namespace AgentPortal.Models.AgentDocuments;

public sealed class AgentDocumentsViewModel
{
    public int DefaultYear { get; set; } = DateTime.UtcNow.Year;
    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<AgentDocumentYearGroupViewModel> YearGroups { get; set; } = Array.Empty<AgentDocumentYearGroupViewModel>();
}

public sealed class AgentDocumentYearGroupViewModel
{
    public int Year { get; set; }
    public IReadOnlyList<AgentDocumentItemViewModel> Documents { get; set; } = Array.Empty<AgentDocumentItemViewModel>();
}

public sealed class AgentDocumentItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedUtc { get; set; }
}
