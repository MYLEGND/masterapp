using System.Collections.Concurrent;
using System.Text;

namespace Legend.Commerce;

/// <summary>
/// One process-local lock per data file and durable same-directory atomic
/// replacement. Backward-compatible JSON and legacy paths remain unchanged.
/// This does NOT substitute for a distributed lease across multiple hosts.
/// </summary>
public static class CommerceAutomationFileStore
{
    private static readonly ConcurrentDictionary<string, object> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static object SyncRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Data path required.", nameof(path));
        return Locks.GetOrAdd(Path.GetFullPath(path), static _ => new object());
    }

    public static string ReadOrCreate(string path, string initialJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialJson);
        lock (SyncRoot(path))
        {
            if (!File.Exists(path))
                WriteAtomic(path, initialJson, overwrite: false);
            return File.ReadAllText(path);
        }
    }

    public static void WriteAtomic(string path, string json, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);
        lock (SyncRoot(path))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Data directory unavailable.");
            Directory.CreateDirectory(directory);
            var staging = Path.Combine(directory, "." + Path.GetFileName(path) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var content = new UTF8Encoding(false).GetBytes(json);
                using (var file = new FileStream(staging, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                {
                    file.Write(content);
                    file.Flush(flushToDisk: true);
                }
                try { File.Move(staging, path, overwrite); }
                catch (IOException) when (!overwrite && File.Exists(path))
                {
                    // Another writer created the initial file. Never replace it.
                }
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
    }
}
