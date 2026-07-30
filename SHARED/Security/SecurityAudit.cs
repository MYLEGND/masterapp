using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Shared.Security;

/// <summary>
/// Stable, shared security audit event contract. Applications emit these for
/// security-relevant actions with a consistent envelope. This is a LOGGING
/// contract — it standardizes structure and redaction, and does not claim
/// storage-level immutability.
/// </summary>
public sealed record SecurityAuditEvent
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Well-known value from <see cref="SecurityAuditEventTypes"/>, or an app-specific type.</summary>
    public required string EventType { get; init; }

    /// <summary>One of <see cref="SecurityAuditResults"/>.</summary>
    public required string Result { get; init; }

    /// <summary>Stable internal actor id (e.g. canonical OID). Never an email/UPN secret.</summary>
    public string? ActorId { get; init; }

    /// <summary>Actor type (agent, client, founder, mobile, system, anonymous).</summary>
    public string? ActorType { get; init; }

    /// <summary>Resource/subject id the action targeted.</summary>
    public string? Resource { get; init; }

    /// <summary>Originating application (AgentPortal, ClientApp, ParfaitApp, Protect-Website).</summary>
    public string? SourceApplication { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Safe, non-sensitive metadata. Values are redacted on emit.</summary>
    public IReadOnlyDictionary<string, string?>? Metadata { get; init; }
}

/// <summary>Well-known audit event types (extend per application as needed).</summary>
public static class SecurityAuditEventTypes
{
    public const string AuthenticationSuccess = "authentication.success";
    public const string AuthenticationFailure = "authentication.failure";
    public const string AuthorizationDenied = "authorization.denied";
    public const string FounderAccess = "founder.access";
    public const string EffectiveContextChanged = "effective_context.changed";
    public const string IdentityLinkChanged = "identity.link_changed";
    public const string PaymentMethodChanged = "payment_method.changed";
    public const string SubscriptionAdministered = "subscription.administered";
    public const string WebhookRejected = "webhook.rejected";
    public const string ReplayRejected = "replay.rejected";
    public const string UploadRejected = "upload.rejected";
    public const string ConfigurationValidationFailed = "configuration.validation_failed";
    public const string SecretValidationFailed = "secret.validation_failed";
}

/// <summary>Well-known audit results.</summary>
public static class SecurityAuditResults
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
}

/// <summary>
/// Emits a <see cref="SecurityAuditEvent"/> through <see cref="ILogger"/> with a
/// stable structured template. Metadata values are redacted via
/// <see cref="LogRedactor"/> so a caller cannot accidentally log a secret.
/// </summary>
public static class SecurityAuditLog
{
    public static void LogSecurityAudit(this ILogger logger, SecurityAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(auditEvent);

        string? safeMetadata = null;
        if (auditEvent.Metadata is { Count: > 0 })
        {
            var parts = new List<string>(auditEvent.Metadata.Count);
            foreach (var kvp in auditEvent.Metadata)
                parts.Add($"{kvp.Key}={LogRedactor.Redact(kvp.Value)}");
            safeMetadata = string.Join("; ", parts);
        }

        logger.LogInformation(
            "SECURITY_AUDIT {EventType} {Result} actor={ActorId} actorType={ActorType} " +
            "resource={Resource} app={SourceApplication} correlationId={CorrelationId} " +
            "timestamp={TimestampUtc} metadata={Metadata}",
            auditEvent.EventType,
            auditEvent.Result,
            auditEvent.ActorId,
            auditEvent.ActorType,
            auditEvent.Resource,
            auditEvent.SourceApplication,
            auditEvent.CorrelationId,
            auditEvent.TimestampUtc,
            safeMetadata);
    }
}
