using Domain.Social;
namespace Infrastructure.Social;

public sealed partial class SocialFeedService
{
    public async Task<SocialOperationResult<SocialPostView>> GetPostAsync(SocialFeedActor actor,
        Guid postId, CancellationToken cancellationToken = default)
    {
        if (!await IsValidActorAsync(actor, cancellationToken))
            return SocialOperationResult<SocialPostView>.Failure("social_actor_invalid", "This post is unavailable.");
        var post = await GetVisiblePostAsync(actor, postId, cancellationToken);
        return post is null
            ? SocialOperationResult<SocialPostView>.Failure("social_post_unavailable", "This post is unavailable.")
            : SocialOperationResult<SocialPostView>.Success(await BuildPostViewAsync(post, actor, cancellationToken));
    }
}
