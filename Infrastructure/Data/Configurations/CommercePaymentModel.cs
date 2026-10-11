using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Configurations;

public static class CommercePaymentModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommerceOrder>().HasIndex(x=>new {x.CommerceBusinessId,x.CheckoutAttemptId})
            .IsUnique().HasFilter("[CheckoutAttemptId] IS NOT NULL");
        modelBuilder.Entity<CommerceMerchantConnection>(e =>
        {
            e.ToTable("CommerceMerchantConnections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.AccountId).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocationId).HasMaxLength(100);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.ProtectedCredential).IsRequired();
            e.Property(x => x.GrantedScopesJson).IsRequired();
            e.Property(x => x.ReadinessJson).IsRequired();
            e.Property(x => x.State).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.Provider, x.Environment, x.AccountId }).IsUnique();
            e.HasIndex(x => x.CommerceBusinessId);
            e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x=>x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommerceMerchantConnectionRevision>(e =>
        {
            e.ToTable("CommerceMerchantConnectionRevisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.AccountId).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocationId).HasMaxLength(100);
            e.Property(x => x.ProtectedCredential).IsRequired();
            e.Property(x => x.State).HasMaxLength(40).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.Property(x => x.GrantedScopesJson).IsRequired();
            e.Property(x => x.ReadinessJson).IsRequired();
            e.HasIndex(x => new { x.ConnectionId, x.Revision }).IsUnique();
            e.HasOne<CommerceMerchantConnection>().WithMany().HasForeignKey(x=>x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommerceMerchantConfiguration>(e =>
        {
            e.ToTable("CommerceMerchantConfigurations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.CommerceBusinessId, x.Environment }).IsUnique();
            e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x=>x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CommerceMerchantConnection>().WithMany().HasForeignKey(x=>x.DefaultConnectionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommerceMerchantAuthorization>(e =>
        {
            e.ToTable("CommerceMerchantAuthorizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProtectedReturnTicket).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
            e.Property(x => x.StateHash).HasMaxLength(100).IsRequired();
            e.Property(x => x.CallbackUrl).HasMaxLength(2000).IsRequired();
            e.Property(x => x.ExpectedAccountId).HasMaxLength(200);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.StateHash).IsUnique();
            e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x=>x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommercePaymentAttempt>(e =>
        {
            e.ToTable("CommercePaymentAttempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.AccountId).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocationId).HasMaxLength(100);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(100).IsRequired();
            e.Property(x => x.State).HasMaxLength(100).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(200);
            e.Property(x => x.PaymentReference).HasMaxLength(200);
            e.Property(x => x.CheckoutUrl).HasMaxLength(2000);
            e.Property(x => x.DisputeState).HasMaxLength(100);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.CommerceOrderId).IsUnique();
            e.HasIndex(x => new { x.Provider, x.Environment, x.AccountId, x.IdempotencyKey }).IsUnique();
            e.HasOne<CommerceOrder>().WithMany().HasForeignKey(x => x.CommerceOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CommerceMerchantConnectionRevision>().WithMany().HasForeignKey(x => x.ConnectionRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommercePaymentReceipt>(e =>
        {
            e.ToTable("CommercePaymentReceipts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            e.Property(x => x.AccountId).HasMaxLength(200).IsRequired();
            e.Property(x => x.EventId).HasMaxLength(200).IsRequired();
            e.Property(x => x.State).HasMaxLength(100).IsRequired();
            e.Property(x => x.SafeErrorCode).HasMaxLength(100);
            e.HasIndex(x => new { x.Provider, x.Environment, x.AccountId, x.EventId }).IsUnique();
            e.HasOne<CommercePaymentAttempt>().WithMany().HasForeignKey(x=>x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommercePaymentEffect>(e =>
        {
            e.ToTable("CommercePaymentEffects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.State).HasMaxLength(100).IsRequired();
            e.Property(x => x.SafeErrorCode).HasMaxLength(100);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.AttemptId, x.Kind }).IsUnique();
            e.HasOne<CommercePaymentAttempt>().WithMany().HasForeignKey(x=>x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CommercePaymentRefund>(e =>
        {
            e.ToTable("CommercePaymentRefunds");
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.State).HasMaxLength(100).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(200);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.AttemptId, x.IdempotencyKey }).IsUnique();
            e.HasOne<CommercePaymentAttempt>().WithMany().HasForeignKey(x=>x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
