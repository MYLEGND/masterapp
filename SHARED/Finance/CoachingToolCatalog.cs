using System.Globalization;

namespace Shared.Finance;

public sealed class CoachingToolDefinition
{
    public string File { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
}

public static class CoachingToolCatalog
{
    public static IReadOnlyList<CoachingToolDefinition> Load(string folderPath, string imageBasePath = "/images/illustrations")
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return Array.Empty<CoachingToolDefinition>();

        Directory.CreateDirectory(folderPath);

        var tools = Directory.EnumerateFiles(folderPath)
            .Where(IsSupportedImage)
            .Where(path => !Path.GetFileName(path).Equals("Legend-Framework.png", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var file = Path.GetFileName(path);
                var label = Path.GetFileNameWithoutExtension(file).Replace("-", " ").Trim();
                var version = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);

                return new CoachingToolDefinition
                {
                    File = file,
                    Label = label,
                    ImageUrl = $"{imageBasePath.TrimEnd('/')}/{Uri.EscapeDataString(file)}?v={version}"
                };
            })
            .OrderBy(tool => tool.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tools;
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
