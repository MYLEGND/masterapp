using Microsoft.AspNetCore.Mvc;
namespace AgentPortal.Mobile;

public sealed partial class MobileSocialController
{
    [HttpGet("posts/{postId:guid}")]
    public async Task<IActionResult> GetSharedPost(Guid postId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveSocialActorAsync(cancellationToken);
        if (resolved.Error is not null) return resolved.Error;
        var result = await _social.GetPostAsync(resolved.Actor!, postId, cancellationToken);
        if (!result.Succeeded || result.Value is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await ToPostDtoAsync(result.Value, cancellationToken));
    }
}
