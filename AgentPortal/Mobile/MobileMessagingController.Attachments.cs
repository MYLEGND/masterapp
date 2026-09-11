using Domain.Messaging;
using Microsoft.AspNetCore.Mvc;
namespace AgentPortal.Mobile;

public sealed partial class MobileMessagingController
{
    [HttpGet("messaging/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DownloadAttachment(Guid attachmentId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveActorAsync(cancellationToken);
        if (resolved.Error is not null) return resolved.Error;
        var result = await _messaging.GetAttachmentForDownloadAsync(
            new MessagingAttachmentDownloadCommand(resolved.Actor!.Actor, attachmentId), cancellationToken);
        if (!result.Succeeded || result.Attachment is null)
            return MessagingFailure(result.ErrorCode, result.ErrorMessage);
        var content = await _attachmentStorage.OpenReadAsync(result.Attachment.StoragePath, cancellationToken);
        if (content is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return File(content, result.Attachment.ContentType, result.Attachment.OriginalFileName,
            enableRangeProcessing: true);
    }
}
