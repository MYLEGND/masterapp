using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Infrastructure.Security.UploadValidation;

/// <summary>
/// Result of a shared upload validation.
/// </summary>
public sealed record UploadValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    string? DetectedContentType)
{
    public static UploadValidationResult Valid(string? detectedContentType) =>
        new(true, null, null, detectedContentType);

    public static UploadValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

/// <summary>
/// A configurable upload policy. Applications pass their own allowed types/limits
/// so the ONE validation authority can serve different upload surfaces without
/// duplicating logic.
/// </summary>
public sealed class UploadValidationPolicy
{
    public long MaxSizeBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Lowercase, dot-prefixed extensions (e.g. ".png"). Empty = any.</summary>
    public IReadOnlySet<string> AllowedExtensions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lowercase content types (e.g. "image/png"). Empty = any.</summary>
    public IReadOnlySet<string> AllowedContentTypes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, the actual bytes must match a recognized signature.</summary>
    public bool RequireKnownSignature { get; init; } = true;

    /// <summary>Standard image policy (PNG/JPEG/WEBP) with a byte-signature requirement.</summary>
    public static UploadValidationPolicy Images(long maxSizeBytes) => new()
    {
        MaxSizeBytes = maxSizeBytes,
        AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".webp" },
        AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "image/png", "image/jpeg", "image/jpg", "image/webp" },
        RequireKnownSignature = true
    };

    /// <summary>Standard PDF-document policy with a %PDF signature requirement.</summary>
    public static UploadValidationPolicy Pdf(long maxSizeBytes) => new()
    {
        MaxSizeBytes = maxSizeBytes,
        AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" },
        AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
        RequireKnownSignature = true
    };
}

/// <summary>
/// The one shared upload validation authority. Centralizes extension validation,
/// content-type (magic-number/signature) validation, maximum size enforcement,
/// dangerous-extension rejection, filename normalization, and path-traversal
/// prevention. It does not perform storage or media processing — callers keep
/// their existing storage/pipeline and use this to validate first.
/// </summary>
public static class UploadValidator
{
    // Extensions that must never be accepted regardless of policy (executable or
    // browser-scriptable content that could enable stored-XSS / RCE if served).
    private static readonly HashSet<string> DangerousExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".msi", ".msp",
            ".sh", ".bash", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
            ".jar", ".jsp", ".php", ".phtml", ".asp", ".aspx", ".ashx", ".cshtml",
            ".html", ".htm", ".xhtml", ".svg", ".xml", ".xsl", ".hta", ".reg"
        };

    /// <summary>
    /// Validates already-buffered upload content against a policy. Callers that
    /// stream to storage should validate the buffered header/content they hold.
    /// </summary>
    public static UploadValidationResult ValidateContent(
        byte[] content,
        string? fileName,
        string? declaredContentType,
        UploadValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (content is null || content.Length == 0)
            return UploadValidationResult.Invalid("UPLOAD_EMPTY", "The uploaded file is empty.");

        if (content.LongLength > policy.MaxSizeBytes)
            return UploadValidationResult.Invalid("UPLOAD_TOO_LARGE", "The uploaded file exceeds the maximum allowed size.");

        var safeName = SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();

        if (IsDangerousExtension(extension))
            return UploadValidationResult.Invalid("UPLOAD_EXTENSION_BLOCKED", "This file type is not permitted.");

        if (policy.AllowedExtensions.Count > 0 &&
            (string.IsNullOrEmpty(extension) || !policy.AllowedExtensions.Contains(extension)))
        {
            return UploadValidationResult.Invalid("UPLOAD_EXTENSION_INVALID", "This file type is not permitted.");
        }

        var detected = DetectContentType(content);

        if (policy.RequireKnownSignature && detected is null)
            return UploadValidationResult.Invalid("UPLOAD_SIGNATURE_UNRECOGNIZED", "The file content does not match a supported type.");

        if (detected is not null)
        {
            if (policy.AllowedContentTypes.Count > 0 && !policy.AllowedContentTypes.Contains(detected))
                return UploadValidationResult.Invalid("UPLOAD_CONTENT_TYPE_INVALID", "This file type is not permitted.");

            // The declared extension must be consistent with the actual bytes.
            if (!string.IsNullOrEmpty(extension) && !ExtensionMatchesContentType(extension, detected))
                return UploadValidationResult.Invalid("UPLOAD_CONTENT_MISMATCH", "The file content does not match its extension.");
        }

        return UploadValidationResult.Valid(detected);
    }

    /// <summary>
    /// Validates upload METADATA only (size, extension allow-list, dangerous
    /// extensions, filename sanitization) without reading content. Intended for
    /// streaming upload paths that must not buffer the whole file — it centralizes
    /// the non-content validation components while signature validation remains
    /// out of scope for those paths.
    /// </summary>
    public static UploadValidationResult ValidateMetadata(string? fileName, long sizeBytes, UploadValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (sizeBytes <= 0)
            return UploadValidationResult.Invalid("UPLOAD_EMPTY", "The uploaded file is empty.");

        if (sizeBytes > policy.MaxSizeBytes)
            return UploadValidationResult.Invalid("UPLOAD_TOO_LARGE", "The uploaded file exceeds the maximum allowed size.");

        var extension = Path.GetExtension(SanitizeFileName(fileName)).ToLowerInvariant();

        if (IsDangerousExtension(extension))
            return UploadValidationResult.Invalid("UPLOAD_EXTENSION_BLOCKED", "This file type is not permitted.");

        if (policy.AllowedExtensions.Count > 0 &&
            (string.IsNullOrEmpty(extension) || !policy.AllowedExtensions.Contains(extension)))
        {
            return UploadValidationResult.Invalid("UPLOAD_EXTENSION_INVALID", "This file type is not permitted.");
        }

        return UploadValidationResult.Valid(null);
    }

    /// <summary>
    /// Validates buffered image content by size and actual byte signature only
    /// (filename-agnostic). Intended for surfaces that store raw bytes + a
    /// content type (e.g. avatars) where the client filename is irrelevant, so a
    /// legitimate image with any/no filename is preserved while non-image bytes
    /// masquerading via a declared Content-Type are rejected.
    /// </summary>
    public static UploadValidationResult ValidateImageContent(byte[] content, UploadValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (content is null || content.Length == 0)
            return UploadValidationResult.Invalid("UPLOAD_EMPTY", "The uploaded file is empty.");

        if (content.LongLength > policy.MaxSizeBytes)
            return UploadValidationResult.Invalid("UPLOAD_TOO_LARGE", "The uploaded file exceeds the maximum allowed size.");

        var detected = DetectContentType(content);
        if (detected is null)
            return UploadValidationResult.Invalid("UPLOAD_SIGNATURE_UNRECOGNIZED", "The file content is not a recognized image.");

        if (policy.AllowedContentTypes.Count > 0 && !policy.AllowedContentTypes.Contains(detected))
            return UploadValidationResult.Invalid("UPLOAD_CONTENT_TYPE_INVALID", "This image type is not permitted.");

        return UploadValidationResult.Valid(detected);
    }

    /// <summary>
    /// Returns a filename with any directory components and traversal sequences
    /// removed. Never returns a rooted path or one containing separators.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        // Strip any path the client may have embedded (handles both separators).
        var name = fileName.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
            name = name[(lastSlash + 1)..];

        name = Path.GetFileName(name).Trim();

        // Defense-in-depth: a filename may not itself be a traversal token.
        if (name is "." or ".." || name.Contains(".."))
            name = name.Replace("..", string.Empty);

        return name;
    }

    public static bool IsDangerousExtension(string? extension)
        => !string.IsNullOrWhiteSpace(extension) && DangerousExtensions.Contains(extension.Trim());

    /// <summary>
    /// Detects a canonical content type from magic-number/file signature, or null
    /// when the bytes do not match a known signature.
    /// </summary>
    public static string? DetectContentType(byte[] content)
    {
        if (content is null || content.Length < 4)
            return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (content.Length >= 8 &&
            content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 &&
            content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A)
            return "image/png";

        // JPEG: FF D8 FF
        if (content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
            return "image/jpeg";

        // GIF: "GIF8"
        if (content[0] == 0x47 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x38)
            return "image/gif";

        // PDF: "%PDF"
        if (content[0] == 0x25 && content[1] == 0x50 && content[2] == 0x44 && content[3] == 0x46)
            return "application/pdf";

        // WEBP: "RIFF"...."WEBP"
        if (content.Length >= 12 &&
            content[0] == 0x52 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x46 &&
            content[8] == 0x57 && content[9] == 0x45 && content[10] == 0x42 && content[11] == 0x50)
            return "image/webp";

        // MP4 / MOV / other ISO base media: bytes 4..8 == "ftyp"
        if (content.Length >= 12 &&
            content[4] == 0x66 && content[5] == 0x74 && content[6] == 0x79 && content[7] == 0x70)
            return "video/mp4";

        return null;
    }

    private static bool ExtensionMatchesContentType(string extension, string detectedContentType)
    {
        return detectedContentType switch
        {
            "image/png" => extension is ".png",
            "image/jpeg" => extension is ".jpg" or ".jpeg",
            "image/gif" => extension is ".gif",
            "image/webp" => extension is ".webp",
            "application/pdf" => extension is ".pdf",
            "video/mp4" => extension is ".mp4" or ".m4v" or ".mov",
            _ => true
        };
    }
}
