using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Social;

/// <summary>
/// The single-server post-upload video processor. It runs inside the upload
/// request only after the complete MP4 has been durably written to local
/// storage. No worker, playlist, or second delivery pipeline is involved.
/// </summary>
internal sealed class LocalFfmpegSocialVideoProcessor
{
    private const long DefaultMaximumOutputBytes = 100L * 1024L * 1024L;
    // Keep the application-owned timeout below the gateway allowance so an
    // upload ends with a controlled Legend error instead of a dropped socket.
    private const int DefaultTimeoutSeconds = 110;
    private const int MaximumDiagnosticLength = 2_000;

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly long _maximumOutputBytes;
    private readonly ILogger _logger;

    public LocalFfmpegSocialVideoProcessor(
        IConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _executablePath = ResolveExecutablePath(configuration[
            "Social:Media:FFmpeg:ExecutablePath"]);
        _timeout = TimeSpan.FromSeconds(ParsePositiveInt(
            configuration["Social:Media:FFmpeg:TimeoutSeconds"],
            DefaultTimeoutSeconds));
        _maximumOutputBytes = ParsePositiveLong(
            configuration["Social:Media:MaximumBytes"],
            DefaultMaximumOutputBytes);
        _logger = logger;
    }

    /// <summary>
    /// Produces a fast-start MP4 in the source directory and atomically replaces
    /// the original only after FFmpeg exits successfully. The process receives
    /// argument tokens directly; no input filename is ever interpolated into a
    /// shell command.
    /// </summary>
    public async Task<SocialVideoProcessingResult> OptimizeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return SocialVideoProcessingResult.Failure(
                "SOCIAL_VIDEO_SOURCE_UNAVAILABLE",
                "Legend could not find the uploaded video for optimization.");
        }

        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return SocialVideoProcessingResult.Failure(
                "SOCIAL_VIDEO_PATH_INVALID",
                "Legend could not prepare the uploaded video for delivery.");
        }

        var outputPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(sourcePath)}.optimized-{Guid.NewGuid():N}.mp4");

        try
        {
            // Equivalent command, passed as structured arguments for safety:
            // ffmpeg -nostdin -y -i input.mp4 -c:v copy
            //   -af loudnorm=I=-14:TP=-1:LRA=11 -c:a aac -b:a 128k
            //   -movflags +faststart output.mp4
            using var process = new Process
            {
                StartInfo = CreateStartInfo(sourcePath, outputPath),
                EnableRaisingEvents = false
            };

            try
            {
                if (!process.Start())
                {
                    return SocialVideoProcessingResult.Failure(
                        "SOCIAL_VIDEO_PROCESSING_UNAVAILABLE",
                        "Legend video optimization could not start. Please try again shortly.");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogError(
                    ex,
                    "FFmpeg could not be started for local social media processing. Executable={Executable}",
                    _executablePath);
                return SocialVideoProcessingResult.Failure(
                    "SOCIAL_VIDEO_PROCESSING_UNAVAILABLE",
                    "Legend video optimization is temporarily unavailable. Please try again shortly.");
            }

            // Draining both redirected streams independently prevents FFmpeg
            // from blocking on a filled diagnostic pipe. These reads must not
            // inherit the request token: on cancellation we first terminate
            // FFmpeg, then drain its final diagnostics before disposing it.
            var standardError = process.StandardError.ReadToEndAsync();
            var standardOutput = process.StandardOutput.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryStop(process);
                await AwaitProcessOutputAsync(standardOutput, standardError);
                _logger.LogError(
                    "FFmpeg timed out after {TimeoutSeconds} seconds while processing social video. Source={SourcePath}",
                    _timeout.TotalSeconds,
                    sourcePath);
                return SocialVideoProcessingResult.Failure(
                    "SOCIAL_VIDEO_PROCESSING_TIMEOUT",
                    "Legend video optimization took too long. Please choose a shorter video and try again.");
            }
            catch (OperationCanceledException)
            {
                TryStop(process);
                await AwaitProcessOutputAsync(standardOutput, standardError);
                throw;
            }

            var diagnostics = await ReadDiagnosticsAsync(standardOutput, standardError);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                _logger.LogError(
                    "FFmpeg failed while processing social video. ExitCode={ExitCode} Source={SourcePath} Diagnostics={Diagnostics}",
                    process.ExitCode,
                    sourcePath,
                    diagnostics);
                return SocialVideoProcessingResult.Failure(
                    "SOCIAL_VIDEO_PROCESSING_FAILED",
                    "Legend could not optimize this video for playback. Choose another video and try again.");
            }

            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength <= 0 || outputLength > _maximumOutputBytes)
            {
                _logger.LogWarning(
                    "FFmpeg output was outside Legend's permitted media size. Source={SourcePath} OutputBytes={OutputBytes}",
                    sourcePath,
                    outputLength);
                return SocialVideoProcessingResult.Failure(
                    "SOCIAL_VIDEO_SIZE_INVALID",
                    "This video is too large after optimization. Choose a shorter video and try again.");
            }

            // Both files are on the same local volume. The original remains
            // intact until a complete, verified, fast-start replacement exists.
            File.Move(outputPath, sourcePath, overwrite: true);
            return SocialVideoProcessingResult.Success(outputLength);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Legend could not replace a local social video with its optimized copy. Source={SourcePath}",
                sourcePath);
            return SocialVideoProcessingResult.Failure(
                "SOCIAL_VIDEO_PROCESSING_FAILED",
                "Legend could not finalize this video for playback. Please try again.");
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    private ProcessStartInfo CreateStartInfo(string sourcePath, string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("loudnorm=I=-14:TP=-1:LRA=11");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("128k");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);
        return startInfo;
    }

    private static async Task<string> ReadDiagnosticsAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        var output = await standardOutput;
        var error = await standardError;
        var combined = string.Concat(error, output);
        return combined.Length <= MaximumDiagnosticLength
            ? combined
            : combined[..MaximumDiagnosticLength];
    }

    private static async Task AwaitProcessOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        _ = await standardOutput;
        _ = await standardError;
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill attempt.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The original upload remains the authoritative cleanup target.
        }
    }

    private static int ParsePositiveInt(string? configured, int fallback) =>
        int.TryParse(configured, out var value) && value > 0
            ? value
            : fallback;

    private static string ResolveExecutablePath(string? configuredPath)
    {
        var path = configuredPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return ResolvePackagedOrPath("ffmpeg");

        // A bare executable name intentionally uses PATH for local
        // development. A relative path with a directory component is resolved
        // once against the deployed application root, never against a request
        // working directory.
        if (Path.IsPathRooted(path) ||
            (!path.Contains(Path.DirectorySeparatorChar) &&
             !path.Contains(Path.AltDirectorySeparatorChar)))
        {
            return ResolvePackagedOrPath(path);
        }

        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private static string ResolvePackagedOrPath(string executableName)
    {
        var packagedPath = Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "ffmpeg",
            OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

        // Production deployment packages the verified Windows executable here.
        // Developers retain the conventional PATH-based ffmpeg workflow.
        return File.Exists(packagedPath) ? packagedPath : executableName;
    }

    private static long ParsePositiveLong(string? configured, long fallback) =>
        long.TryParse(configured, out var value) && value > 0
            ? value
            : fallback;
}

internal sealed record SocialVideoProcessingResult(
    bool Succeeded,
    long? FileSizeBytes,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static SocialVideoProcessingResult Success(long fileSizeBytes) =>
        new(true, fileSizeBytes, null, null);

    public static SocialVideoProcessingResult Failure(
        string errorCode,
        string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}
