using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;

namespace Infrastructure.Bookings;

/// <summary>
/// Pure saved-metadata projection. Deliberately does not depend on the connection
/// authority: GetAsync can import a connection, and Graph access can refresh it.
/// This reader never resolves a customer, imports credentials, calls Graph or saves.
/// </summary>
public sealed class ReceptionistBookingReadinessReader(DbContextOptions<MasterAppDbContext> options, IConfiguration configuration)
{
    public async Task<bool> MatchesReviewedConnectionAsync(MarketingOwnerScope owner, string expectedFingerprint, CancellationToken ct)
    {
        await using var db = new MasterAppDbContext(options);
        return Matches(await ReadCalendarAsync(db, owner, ct), expectedFingerprint);
    }
    internal static bool Matches(CalendarMetadata? metadata, string fingerprint) =>
        metadata?.ConnectedUtc is not null && metadata.DisconnectedUtc is null &&
        (metadata.Delegated || metadata.Application) && PermissionRecorded(metadata.Permissions) && metadata.Fingerprint == fingerprint;
    public async Task<ReceptionistBookingReadiness> ReadAsync(MarketingOwnerScope owner,
        Guid configurationId, CancellationToken ct = default)
    {
        await using var db = new MasterAppDbContext(options);
        var version = await (from connection in db.MarketingConnections.AsNoTracking()
            join configuration in db.Set<ReceptionistConfigurationVersion>().AsNoTracking()
                on connection.Id equals configuration.MarketingConnectionId
            where connection.OwnerKey == owner.Key && connection.Provider == "openai" &&
                configuration.Id == configurationId && (connection.ReceptionistActiveConfigurationId == configuration.Id ||
                    !db.Set<ReceptionistConfigurationVersion>().Any(v => v.MarketingConnectionId == connection.Id && v.Version > configuration.Version))
            select new { configuration.ConfigurationJson, configuration.ConfigurationSha256,
                connection.Revision, connection.OwnerType, connection.AgentTrackingProfileId, connection.CommerceBusinessId })
            .SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("The exact current owner configuration is unavailable.");
        if (version.OwnerType != owner.OwnerType || version.AgentTrackingProfileId != owner.AgentTrackingProfileId ||
            version.CommerceBusinessId != owner.CommerceBusinessId)
            throw new InvalidOperationException("The recorded owner scope is inconsistent.");
        var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var calendar = await ReadCalendarAsync(db, owner, ct);
        var policy = config.BookingPolicy;
        var target = await ReadTargetAsync(db, owner, policy?.BookingAgentProfileId, ct);
        var requested = policy?.Enabled == true && config.RequestedCapabilities.Contains(ReceptionistCapabilities.CanonicalBooking);
        var connected = calendar?.ConnectedUtc is not null && calendar.DisconnectedUtc is null;
        var permission = PermissionRecorded(calendar?.Permissions);
        var fingerprint = calendar?.Fingerprint;
        var reasons = new List<string>();
        if (!requested) reasons.Add("booking_not_enabled");
        if (calendar is null) reasons.Add("calendar_connection_not_recorded");
        else if (!connected) reasons.Add("calendar_connection_recorded_disconnected");
        if (!permission) reasons.Add("appointment_permission_not_recorded");
        if (policy is not null && fingerprint != policy.ExpectedCalendarConfigurationSha256) reasons.Add("calendar_configuration_not_reviewed");
        if (policy?.Services.Count is not > 0) reasons.Add("approved_service_mapping_missing");
        if (target is null) reasons.Add("explicit_calendar_target_missing");
        else if (!target.Enabled) reasons.Add("booking_agent_disabled");
        if (target is not null && policy?.ExpectedCalendarTargetSha256 != target.Fingerprint) reasons.Add("calendar_target_not_reviewed");
        // A recorded connection/grant is not a fresh provider verification or release acceptance.
        reasons.Add("provider_and_runtime_acceptance_required");
        var definitions = JsonSerializer.Deserialize<ReceptionistApplicationServiceDefinition[]>(config.ServiceDefinitionsJson,
            ReceptionistConfigurationCodec.Json) ?? [];
        var mappings = policy?.Services.Select(x => new ReceptionistBookingServiceStatus(x.ServiceKey,
            definitions.Single(y => y.ServiceKey == x.ServiceKey).DisplayName, x.TimeZoneId, x.DurationMinutes)).ToArray() ?? [];
        var stillCurrent = await db.MarketingConnections.AsNoTracking().AnyAsync(x => x.OwnerKey == owner.Key &&
            x.Provider == "openai" && x.Revision == version.Revision, ct);
        if (!stillCurrent) throw new DbUpdateConcurrencyException("The observed configuration changed during metadata preparation.");
        var calendarAfter = await ReadCalendarAsync(db, owner, ct);
        if (calendarAfter?.Revision != calendar?.Revision || calendarAfter?.Fingerprint != fingerprint)
            throw new DbUpdateConcurrencyException("The calendar metadata changed during preparation.");
        var targetAfter = await ReadTargetAsync(db, owner, policy?.BookingAgentProfileId, ct);
        if (targetAfter != target) throw new DbUpdateConcurrencyException("The calendar target changed during preparation.");
        return new("saved_metadata_only", calendar is not null, connected, calendar?.Revision, fingerprint,
            target?.Enabled, target?.Fingerprint,
            calendar?.Delegated == true ? "delegated" : calendar?.Application == true ? "application" : "unknown",
            permission, calendar?.LastVerifiedUtc, mappings.Length > 0, requested, policy?.QaOnly ?? true,
            policy?.AllowConsentedCustomerEmail == true ? "explicit_customer_email_consent_required" : "external_booking_disabled",
            mappings, reasons);
    }

    private async Task<TargetMetadata?> ReadTargetAsync(MasterAppDbContext db, MarketingOwnerScope owner, Guid? profileId, CancellationToken ct)
    {
        if (profileId is null || profileId == Guid.Empty) return null;
        // Booking configuration columns only; no names, emails, phones, profiles,
        // credentials, client relationships or events are materialized.
        var agent = await db.AgentProfiles.AsNoTracking().Where(x => x.Id == profileId)
            .Select(x => new { x.AgentUserId, x.IsActive, x.BookingEnabled, x.BookingPageIdOrMailbox }).SingleOrDefaultAsync(ct);
        if (agent is null || string.IsNullOrWhiteSpace(agent.AgentUserId)) return null;
        if (owner.CommerceBusinessId is null)
        {
            var actor = await new Leads.WebsiteIntakeRecipientResolver(db, configuration).ResolveCrmOwnerAsync(owner, ct);
            if (actor is null || actor != agent.AgentUserId.Trim().ToLowerInvariant()) return null;
        }
        if (owner.CommerceBusinessId is { } scope && !await BusinessBookingAccess.IsAttachedAgentAsync(db, scope, profileId.Value, ct)) return null;
        if (owner.AgentTrackingProfileId is { } tracking && !await db.AgentTrackingProfiles.AsNoTracking()
            .AnyAsync(x => x.Id == tracking && x.AgentUserId == agent.AgentUserId, ct)) return null;
        var mailbox = owner.CommerceBusinessId is { } business
            ? await db.CommerceBusinessStorefrontSettings.AsNoTracking().Where(x => x.CommerceBusinessId == business).Select(x => x.BookingMailboxId).SingleOrDefaultAsync(ct)
            : null;
        mailbox = string.IsNullOrWhiteSpace(mailbox) ? agent.BookingPageIdOrMailbox : mailbox;
        if (string.IsNullOrWhiteSpace(mailbox)) return null;
        var hash = TargetFingerprint(owner, agent.AgentUserId, mailbox);
        var lifecycleActive = !await db.AccountLifecycleRecords.AsNoTracking().AnyAsync(x => x.UserId == agent.AgentUserId &&
            x.ParticipantType == Domain.Messaging.MessagingParticipantTypes.Agent &&
            (x.ProfileId != profileId || x.State != Domain.Accounts.AccountLifecycleStates.Active), ct);
        return new(agent.IsActive && lifecycleActive && (agent.BookingEnabled ?? !string.IsNullOrWhiteSpace(agent.BookingPageIdOrMailbox)), hash);
    }

    public static string TargetFingerprint(MarketingOwnerScope owner, string actorId, string mailbox) =>
        ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new { owner = owner.Key, actorId, mailbox = mailbox.Trim().TrimEnd('/') }));
    private sealed record TargetMetadata(bool Enabled, string Fingerprint);

    internal static async Task<CalendarMetadata?> ReadCalendarAsync(MasterAppDbContext db, MarketingOwnerScope owner, CancellationToken ct)
    {
        return await db.MarketingConnections.AsNoTracking()
            .Where(x => x.OwnerKey == owner.Key && x.Provider == MicrosoftCalendarConnectionAuthority.Provider)
            .Select(x => new CalendarMetadata(x.Id, x.OwnerKey, x.Revision, x.ConnectedUtc, x.DisconnectedUtc, x.LastVerifiedUtc,
                x.ProviderAuthorizationMethod == MicrosoftCalendarConnectionAuthority.DelegatedAuthorization,
                x.ProviderAuthorizationMethod == MicrosoftCalendarConnectionAuthority.ApplicationAuthorization,
                x.ProviderPermissionsJson))
            .SingleOrDefaultAsync(ct);
    }

    // Row Revision remains the credential concurrency token. Routine token refresh must
    // not invalidate an approved identity/grant configuration, so it is excluded here.
    // Connect/disconnect, owner or recorded permission changes change this fingerprint.
    internal sealed record CalendarMetadata(Guid Id, string OwnerKey, Guid Revision, DateTime? ConnectedUtc,
        DateTime? DisconnectedUtc, DateTime? LastVerifiedUtc, bool Delegated, bool Application, string? Permissions)
    {
        internal string Fingerprint => ReceptionistConfigurationCodec.Hash(JsonSerializer.Serialize(new
        { Id, OwnerKey, ConnectedUtc, DisconnectedUtc, Delegated, Application, Permissions }));
    }

    private static bool PermissionRecorded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) return false;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(value);
            return values?.Contains("BookingsAppointment.ReadWrite.All", StringComparer.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
}
