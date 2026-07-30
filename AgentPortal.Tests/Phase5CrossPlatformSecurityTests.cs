using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.RateLimiting;
using Infrastructure.Security;
using Infrastructure.Security.UploadValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Shared.Security;
using Xunit;

namespace AgentPortal.Tests;

// Phase 5 — Cross-Platform Security Authorities regression coverage.
//   Objective 1: one shared upload validation authority (Infrastructure).
//   Objective 2: one shared rate-limiting authority (Infrastructure).
//   Objective 3: one shared logging-redaction + audit authority (SHARED).
public class Phase5CrossPlatformSecurityTests
{
    // Signature-only byte stubs (enough for magic-number detection).
    private static byte[] Png() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
    private static byte[] Webp() => new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 };
    private static byte[] Pdf() => new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E };

    // =====================================================================
    // OBJECTIVE 1 — Upload validation authority
    // =====================================================================

    [Fact]
    public void Upload_AllowedImage_Accepted()
    {
        var result = UploadValidator.ValidateContent(Png(), "photo.png", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.True(result.IsValid);
        Assert.Equal("image/png", result.DetectedContentType);
    }

    [Fact]
    public void Upload_BlockedExecutableExtension_Rejected()
    {
        var result = UploadValidator.ValidateContent(Png(), "malware.exe", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_EXTENSION_BLOCKED", result.ErrorCode);
    }

    [Fact]
    public void Upload_DoubleExtensionAttack_Rejected()
    {
        // "image.png.exe" — trailing dangerous extension must be caught.
        var result = UploadValidator.ValidateContent(Png(), "image.png.exe", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_EXTENSION_BLOCKED", result.ErrorCode);
    }

    [Fact]
    public void Upload_CaseVariantDangerousExtension_Rejected()
    {
        Assert.True(UploadValidator.IsDangerousExtension(".ExE"));
        Assert.True(UploadValidator.IsDangerousExtension(".JS"));
        var result = UploadValidator.ValidateContent(Png(), "x.PhP", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Upload_MimeAndContentMismatch_Rejected()
    {
        // Extension says .png but bytes are JPEG.
        var result = UploadValidator.ValidateContent(Jpeg(), "actually.png", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_CONTENT_MISMATCH", result.ErrorCode);
    }

    [Fact]
    public void Upload_InvalidMagicBytes_Rejected()
    {
        var notAnImage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var result = UploadValidator.ValidateContent(notAnImage, "photo.png", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_SIGNATURE_UNRECOGNIZED", result.ErrorCode);
    }

    [Fact]
    public void Upload_Oversized_Rejected()
    {
        var result = UploadValidator.ValidateContent(Png(), "photo.png", "image/png", UploadValidationPolicy.Images(4));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_TOO_LARGE", result.ErrorCode);
    }

    [Fact]
    public void Upload_Empty_Rejected()
    {
        var result = UploadValidator.ValidateContent(Array.Empty<byte>(), "photo.png", "image/png", UploadValidationPolicy.Images(3 * 1024 * 1024));
        Assert.False(result.IsValid);
        Assert.Equal("UPLOAD_EMPTY", result.ErrorCode);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\cmd")]
    [InlineData("/etc/shadow")]
    [InlineData("folder/nested/name.png")]
    public void Upload_PathTraversalOrAbsolute_FilenameSanitizedToBasename(string malicious)
    {
        var safe = UploadValidator.SanitizeFileName(malicious);
        Assert.DoesNotContain("/", safe);
        Assert.DoesNotContain("\\", safe);
        Assert.DoesNotContain("..", safe);
    }

    [Fact]
    public void Upload_DifferentAppProfiles_UseSameImplementation()
    {
        // A PDF profile and an image profile both flow through the one validator.
        var pdf = UploadValidator.ValidateContent(Pdf(), "doc.pdf", "application/pdf", UploadValidationPolicy.Pdf(1024 * 1024));
        Assert.True(pdf.IsValid);
        Assert.Equal("application/pdf", pdf.DetectedContentType);

        // A PDF is rejected by the image profile — same implementation, different profile.
        var pdfAsImage = UploadValidator.ValidateContent(Pdf(), "doc.pdf", "application/pdf", UploadValidationPolicy.Images(1024 * 1024));
        Assert.False(pdfAsImage.IsValid);
    }

    [Fact]
    public void Upload_ImageContent_FilenameAgnostic_Path_UsedByAvatars()
    {
        // Mirrors the avatar controller integration: bytes-only image validation.
        Assert.True(UploadValidator.ValidateImageContent(Webp(), UploadValidationPolicy.Images(3 * 1024 * 1024)).IsValid);
        Assert.False(UploadValidator.ValidateImageContent(new byte[] { 9, 9, 9, 9 }, UploadValidationPolicy.Images(3 * 1024 * 1024)).IsValid);
    }

    // =====================================================================
    // OBJECTIVE 2 — Rate-limiting authority
    // =====================================================================

    [Fact]
    public void RateLimit_SharedPolicies_RegisterConsistently()
    {
        var options = new RateLimiterOptions();
        PlatformRateLimiting.ConfigurePolicies(options);

        // Standard rejection behavior applied by the one authority.
        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
        // No global limiter — nothing is throttled unless an endpoint opts in.
        Assert.Null(options.GlobalLimiter);
        // Stable, shared policy names the apps reference.
        Assert.Equal("public-ingest", PlatformRateLimiting.PublicIngestPolicy);
        Assert.Equal("public-form", PlatformRateLimiting.PublicFormPolicy);
    }

    [Fact]
    public void RateLimit_AnonymousPartitionsByIp_AuthenticatedByIdentity()
    {
        var anon = new DefaultHttpContext();
        anon.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        Assert.Equal("203.0.113.7", PlatformRateLimiting.ResolvePartitionKey(anon));

        var authed = new DefaultHttpContext();
        authed.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "agent-oid-xyz") },
                "TestAuth"));
        Assert.Equal("agent-oid-xyz", PlatformRateLimiting.ResolvePartitionKey(authed));
    }

    // =====================================================================
    // OBJECTIVE 3 — Logging redaction + audit contract
    // =====================================================================

    [Fact]
    public void Redaction_SensitiveHeaders_AreMasked()
    {
        Assert.True(LogRedactor.IsSensitiveHeader("Authorization"));
        Assert.True(LogRedactor.IsSensitiveHeader("Cookie"));
        Assert.True(LogRedactor.IsSensitiveHeader("X-Api-Key"));
        Assert.True(LogRedactor.IsSensitiveHeader("RequestVerificationToken"));

        Assert.Equal(LogRedactor.Mask, LogRedactor.RedactHeader("Authorization", "Bearer abc.def.ghi"));
        Assert.Equal(LogRedactor.Mask, LogRedactor.RedactHeader("Cookie", ".AspNetCore.Cookies=secret"));
        Assert.Equal("value", LogRedactor.RedactHeader("X-Correlation-ID", "value")); // non-sensitive preserved
    }

    [Fact]
    public void Redaction_BearerAndJwt_Redacted()
    {
        var msg = "Called API with Authorization: Bearer eyJhbGciOi.eyJzdWIiOi.SflKxwRJ and it worked";
        var redacted = LogRedactor.Redact(msg);
        Assert.DoesNotContain("eyJhbGciOi.eyJzdWIiOi.SflKxwRJ", redacted);
        Assert.Contains(LogRedactor.Mask, redacted!);
    }

    [Fact]
    public void Redaction_TokensSecretsApiKeys_Redacted()
    {
        Assert.DoesNotContain("s3cr3t", LogRedactor.Redact("access_token=s3cr3t"));
        Assert.DoesNotContain("hunter2", LogRedactor.Redact("password=hunter2"));
        Assert.DoesNotContain("AKIA-KEY", LogRedactor.Redact("api_key=AKIA-KEY"));
        Assert.DoesNotContain("topsecret", LogRedactor.Redact("client_secret: topsecret"));
    }

    [Fact]
    public void Redaction_ConnectionString_SecretsMasked()
    {
        var cs = "Server=tcp:db;Database=app;User ID=u;Password=SuperSecret;Encrypt=true";
        var redacted = LogRedactor.RedactConnectionString(cs);
        Assert.DoesNotContain("SuperSecret", redacted);
        Assert.Contains("Server=tcp:db", redacted); // non-secret parts preserved

        var storage = "DefaultEndpointsProtocol=https;AccountName=a;AccountKey=abc123==;EndpointSuffix=core";
        var redactedStorage = LogRedactor.RedactConnectionString(storage);
        Assert.DoesNotContain("abc123==", redactedStorage);
        Assert.Contains("AccountName=a", redactedStorage);
    }

    [Fact]
    public void Redaction_WebhookSignatureAndPaymentToken_NotLeaked()
    {
        // Treated as sensitive values that must be masked before logging.
        Assert.Equal(LogRedactor.Mask, LogRedactor.MaskValue("t=123,v1=abcdef-square-signature"));
        Assert.Equal(LogRedactor.Mask, LogRedactor.MaskValue("cnon:card-nonce-token"));
    }

    [Fact]
    public void Audit_Contract_IsStable_AndRedactsMetadata()
    {
        var recorder = new RecordingLogger();

        recorder.LogSecurityAudit(new SecurityAuditEvent
        {
            EventType = SecurityAuditEventTypes.AuthorizationDenied,
            Result = SecurityAuditResults.Denied,
            ActorId = "agent-oid-1",
            ActorType = "agent",
            Resource = "client-profile-9",
            SourceApplication = "AgentPortal",
            CorrelationId = "corr-123",
            Metadata = new Dictionary<string, string?> { ["note"] = "token=should-be-hidden" }
        });

        Assert.Single(recorder.Messages);
        var logged = recorder.Messages[0];
        Assert.Contains("SECURITY_AUDIT", logged);
        Assert.Contains("authorization.denied", logged);
        Assert.Contains("corr-123", logged);       // safe correlation retained
        Assert.Contains("agent-oid-1", logged);     // safe actor id retained
        Assert.DoesNotContain("should-be-hidden", logged); // metadata secret redacted

        // Stable contract surface.
        Assert.Equal("authorization.denied", SecurityAuditEventTypes.AuthorizationDenied);
        Assert.Equal("denied", SecurityAuditResults.Denied);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
