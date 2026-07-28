using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Configurations;

internal static class SocialFeedModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder, string? providerName)
    {
        var textType = providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
            ? "nvarchar(max)"
            : "TEXT";

        modelBuilder.Entity<SocialPost>(entity =>
        {
            entity.ToTable("SocialPosts");
            entity.HasKey(post => post.Id);
            entity.Property(post => post.AuthorUserId).HasMaxLength(450).IsRequired();
            entity.Property(post => post.AuthorParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(post => post.ContentType).HasMaxLength(40).IsRequired();
            entity.Property(post => post.Audience).HasMaxLength(40).IsRequired();
            entity.Property(post => post.Body).HasColumnType(textType).IsRequired();
            entity.HasIndex(post => new { post.AuthorUserId, post.AuthorParticipantType, post.PostedUtc });
            entity.HasIndex(post => new { post.DeletedUtc, post.ExpiresUtc, post.PostedUtc });
            entity.HasIndex(post => post.RepostOfSocialPostId);
        });

        modelBuilder.Entity<SocialPostMediaAsset>(entity =>
        {
            entity.ToTable("SocialPostMediaAssets");
            entity.HasKey(asset => asset.Id);

            entity.Property(asset => asset.MediaKind)
                .HasMaxLength(40)
                .IsRequired();

            entity.Property(asset => asset.StorageKey)
                .HasMaxLength(1024)
                .IsRequired();

            entity.Property(asset => asset.ThumbnailStorageKey)
                .HasMaxLength(1024);

            entity.Property(asset => asset.MimeType)
                .HasMaxLength(160)
                .IsRequired();

            entity.Property(asset => asset.AspectRatio)
                .HasPrecision(12, 6);

            entity.Property(asset => asset.DurationSeconds)
                .HasPrecision(12, 3);

            entity.Property(asset => asset.ProcessingState)
                .HasMaxLength(40)
                .IsRequired();

            entity.Property(asset => asset.AccessibilityText)
                .HasMaxLength(2000);

            entity.HasIndex(asset => new
            {
                asset.SocialPostId,
                asset.DisplayOrder
            }).IsUnique();

            entity.HasIndex(asset => asset.StorageKey).IsUnique();

            entity.HasIndex(asset => new
            {
                asset.ProcessingState,
                asset.CreatedUtc
            });

            entity.HasOne(asset => asset.SocialPost)
                .WithMany(post => post.MediaAssets)
                .HasForeignKey(asset => asset.SocialPostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SocialPostComment>(entity =>
        {
            entity.ToTable("SocialPostComments");
            entity.HasKey(comment => comment.Id);
            entity.Property(comment => comment.AuthorUserId).HasMaxLength(450).IsRequired();
            entity.Property(comment => comment.AuthorParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(comment => comment.Body).HasColumnType(textType).IsRequired();
            entity.HasIndex(comment => new { comment.SocialPostId, comment.DeletedUtc, comment.CreatedUtc });
            entity.HasIndex(comment => comment.ParentCommentId);
            entity.HasOne(comment => comment.SocialPost)
                .WithMany()
                .HasForeignKey(comment => comment.SocialPostId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(comment => comment.ParentComment)
                .WithMany()
                .HasForeignKey(comment => comment.ParentCommentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SocialPostReaction>(entity =>
        {
            entity.ToTable("SocialPostReactions");
            entity.HasKey(reaction => reaction.Id);
            entity.Property(reaction => reaction.ActorUserId).HasMaxLength(450).IsRequired();
            entity.Property(reaction => reaction.ActorParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(reaction => reaction.ReactionType).HasMaxLength(40).IsRequired();
            entity.HasIndex(reaction => new { reaction.SocialPostId, reaction.ActorUserId, reaction.ActorParticipantType }).IsUnique();
            entity.HasOne(reaction => reaction.SocialPost)
                .WithMany()
                .HasForeignKey(reaction => reaction.SocialPostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SocialFollow>(entity =>
        {
            entity.ToTable("SocialFollows");
            entity.HasKey(follow => follow.Id);
            entity.Property(follow => follow.FollowerUserId).HasMaxLength(450).IsRequired();
            entity.Property(follow => follow.FollowerParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(follow => follow.FollowedUserId).HasMaxLength(450).IsRequired();
            entity.Property(follow => follow.FollowedParticipantType).HasMaxLength(40).IsRequired();
            entity.HasIndex(follow => new
            {
                follow.FollowerUserId,
                follow.FollowerParticipantType,
                follow.FollowedUserId,
                follow.FollowedParticipantType
            }).IsUnique();
            entity.HasIndex(follow => new { follow.FollowedUserId, follow.FollowedParticipantType });
            entity.HasIndex(follow => follow.SourceSocialPostId);
        });

        modelBuilder.Entity<SocialPostViewer>(entity =>
        {
            entity.ToTable("SocialPostViews");
            entity.HasKey(view => view.Id);
            entity.Property(view => view.ViewerUserId).HasMaxLength(450).IsRequired();
            entity.Property(view => view.ViewerParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(view => view.MaximumWatchDurationSeconds).HasPrecision(12, 3);
            entity.Property(view => view.MaximumWatchCompletionPercentage).HasPrecision(5, 2);
            entity.HasIndex(view => new { view.SocialPostId, view.ViewerUserId, view.ViewerParticipantType }).IsUnique();
            entity.HasIndex(view => new { view.SocialPostId, view.FirstViewedUtc });
            entity.HasOne(view => view.SocialPost).WithMany().HasForeignKey(view => view.SocialPostId).OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureActorPostEntity<SocialPostSave>(modelBuilder, "SocialPostSaves");
        ConfigureActorPostEntity<SocialPostShare>(modelBuilder, "SocialPostShares");
        ConfigureActorPostEntity<SocialPostRepost>(modelBuilder, "SocialPostReposts");

        modelBuilder.Entity<SocialProfileVisit>(entity =>
        {
            entity.ToTable("SocialProfileVisits");
            entity.HasKey(visit => visit.Id);
            entity.Property(visit => visit.TargetUserId).HasMaxLength(450).IsRequired();
            entity.Property(visit => visit.TargetParticipantType).HasMaxLength(40).IsRequired();
            entity.Property(visit => visit.VisitorUserId).HasMaxLength(450).IsRequired();
            entity.Property(visit => visit.VisitorParticipantType).HasMaxLength(40).IsRequired();
            entity.HasIndex(visit => new { visit.TargetUserId, visit.TargetParticipantType, visit.VisitorUserId, visit.VisitorParticipantType, visit.SourceSocialPostId }).IsUnique();
            entity.HasIndex(visit => new { visit.TargetUserId, visit.TargetParticipantType, visit.FirstVisitedUtc });
        });

        modelBuilder.Entity<SocialPostMusicAttachment>(entity =>
        {
            entity.ToTable("SocialPostMusicAttachments");
            entity.HasKey(music => music.Id);
            entity.Property(music => music.ProviderId).HasMaxLength(80).IsRequired();
            entity.Property(music => music.ProviderTrackId).HasMaxLength(256).IsRequired();
            entity.Property(music => music.TrackTitle).HasMaxLength(400).IsRequired();
            entity.Property(music => music.ArtistName).HasMaxLength(400).IsRequired();
            entity.Property(music => music.PreviewUrl).HasMaxLength(2048);
            entity.Property(music => music.TrackDurationSeconds).HasPrecision(12, 3);
            entity.Property(music => music.TrimStartSeconds).HasPrecision(12, 3);
            entity.Property(music => music.TrimEndSeconds).HasPrecision(12, 3);
            entity.Property(music => music.MusicVolume).HasPrecision(5, 2);
            entity.Property(music => music.OriginalAudioVolume).HasPrecision(5, 2);
            entity.HasIndex(music => music.SocialPostId).IsUnique();
            entity.HasIndex(music => new { music.ProviderId, music.ProviderTrackId });
            entity.HasOne(music => music.SocialPost).WithOne(post => post.MusicAttachment).HasForeignKey<SocialPostMusicAttachment>(music => music.SocialPostId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureActorPostEntity<TEntity>(
        ModelBuilder modelBuilder,
        string tableName)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey("Id");
            entity.Property<string>("ActorUserId").HasMaxLength(450).IsRequired();
            entity.Property<string>("ActorParticipantType").HasMaxLength(40).IsRequired();
            entity.HasIndex("SocialPostId", "ActorUserId", "ActorParticipantType").IsUnique();
            entity.HasIndex("SocialPostId", "CreatedUtc");
            entity.HasOne(typeof(SocialPost), "SocialPost").WithMany().HasForeignKey("SocialPostId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}
