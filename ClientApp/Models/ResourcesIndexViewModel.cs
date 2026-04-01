using System;
using System.Collections.Generic;

namespace ClientApp.Models;

public sealed class ResourceLibraryItem
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string Extension { get; init; }
}

public sealed class PolicyDocumentItem
{
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime UploadedUtc { get; init; }
    public required string PreviewUrl { get; init; }
    public required string DownloadUrl { get; init; }
}

public sealed class ResourcesIndexViewModel
{
    public IReadOnlyList<ResourceLibraryItem> Resources { get; init; } = Array.Empty<ResourceLibraryItem>();
    public IReadOnlyList<PolicyDocumentItem> PolicyDocuments { get; init; } = Array.Empty<PolicyDocumentItem>();
}