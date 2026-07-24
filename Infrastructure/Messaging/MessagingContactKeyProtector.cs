using System.Security.Cryptography;
using System.Text.Json;
using Domain.Messaging;
using Microsoft.AspNetCore.DataProtection;

namespace Infrastructure.Messaging;

public interface IMessagingContactKeyProtector
{
    string Protect(MessagingActor viewer, MessagingRecipientSummary recipient);

    bool TryUnprotect(
        MessagingActor viewer,
        string? contactKey,
        out MessagingParticipantReference participant);
}

internal sealed class MessagingContactKeyProtector : IMessagingContactKeyProtector
{
    private const string Purpose = "MasterApp.Messaging.ContactReference.v1";
    private readonly IDataProtector _protector;

    public MessagingContactKeyProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(MessagingActor viewer, MessagingRecipientSummary recipient)
    {
        var payload = new ContactKeyPayload(
            Normalize(viewer.UserId),
            viewer.ParticipantType.Trim(),
            Normalize(recipient.UserId),
            recipient.ParticipantType.Trim());
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public bool TryUnprotect(
        MessagingActor viewer,
        string? contactKey,
        out MessagingParticipantReference participant)
    {
        participant = new MessagingParticipantReference(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(contactKey))
            return false;

        try
        {
            var payload = JsonSerializer.Deserialize<ContactKeyPayload>(_protector.Unprotect(contactKey));
            if (payload is null ||
                !string.Equals(payload.ViewerUserId, Normalize(viewer.UserId), StringComparison.Ordinal) ||
                !string.Equals(payload.ViewerParticipantType, viewer.ParticipantType.Trim(), StringComparison.Ordinal) ||
                !IsParticipantType(payload.ParticipantType) ||
                string.IsNullOrWhiteSpace(payload.ParticipantUserId))
            {
                return false;
            }

            participant = new MessagingParticipantReference(
                Normalize(payload.ParticipantUserId),
                payload.ParticipantType);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsParticipantType(string participantType) =>
        participantType == MessagingParticipantTypes.Agent ||
        participantType == MessagingParticipantTypes.Client;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private sealed record ContactKeyPayload(
        string ViewerUserId,
        string ViewerParticipantType,
        string ParticipantUserId,
        string ParticipantType);
}
