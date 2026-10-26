using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public static partial class ReceptionistModelConfiguration
{
    private static void ConfigureManagement(ModelBuilder model, string collation)
    {
        void Text(EntityTypeBuilder entity, string name, int size, bool required = true) =>
            entity.Property<string>(name).HasMaxLength(size).UseCollation(collation).IsRequired(required);
        var credential = model.Entity<ReceptionistCredentialVersion>();
        credential.ToTable("ReceptionistCredentialVersions");
        credential.HasKey(x => x.Id);
        credential.HasAlternateKey(x => new { x.MarketingConnectionId, x.Id });
        credential.HasOne<MarketingConnection>().WithMany().HasForeignKey(x => x.MarketingConnectionId).OnDelete(DeleteBehavior.Restrict);
        Text(credential, nameof(ReceptionistCredentialVersion.Provider), 50);
        Text(credential, nameof(ReceptionistCredentialVersion.ProviderProjectKey), 200);
        Text(credential, nameof(ReceptionistCredentialVersion.Purpose), 50);
        Text(credential, nameof(ReceptionistCredentialVersion.ProtectedValue), 32768);
        Text(credential, nameof(ReceptionistCredentialVersion.ValueSha256), 64);
        Text(credential, nameof(ReceptionistCredentialVersion.CreatedByActorId), 450);
        credential.Property(x => x.Revision).IsConcurrencyToken();

        var package = model.Entity<ReceptionistOperationPackage>();
        package.ToTable("ReceptionistOperationPackages", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistOperationPackage_Lifetime", "[CreatedUtc] < [ExpiresUtc]");
            t.HasCheckConstraint("CK_ReceptionistOperationPackage_Review", "[ReviewState] IN ('pending','accepted','rejected') AND ([ReviewState] = 'pending' OR ([ReviewedByActorId] IS NOT NULL AND [ReviewedUtc] IS NOT NULL AND [ReviewedPayloadSha256] IS NOT NULL AND [ReviewedPayloadSha256] = [PayloadSha256]))");
        });
        package.HasKey(x => x.Id);
        package.HasOne<ReceptionistConfigurationVersion>().WithMany().HasForeignKey(x => new { x.MarketingConnectionId, x.ConfigurationVersionId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        package.HasOne<ReceptionistCredentialVersion>().WithMany().HasForeignKey(x => new { x.MarketingConnectionId, x.ApiCredentialVersionId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        package.HasOne<ReceptionistCredentialVersion>().WithMany().HasForeignKey(x => new { x.MarketingConnectionId, x.WebhookCredentialVersionId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Text(package, nameof(ReceptionistOperationPackage.Operation), 100);
        Text(package, nameof(ReceptionistOperationPackage.PayloadJson), 262144);
        Text(package, nameof(ReceptionistOperationPackage.PayloadSha256), 64);
        Text(package, nameof(ReceptionistOperationPackage.CreatedByActorId), 450);
        Text(package, nameof(ReceptionistOperationPackage.ReviewState), 20);
        Text(package, nameof(ReceptionistOperationPackage.ReviewedByActorId), 450, false);
        Text(package, nameof(ReceptionistOperationPackage.ReviewedPayloadSha256), 64, false);
        package.Property(x => x.Revision).IsConcurrencyToken();
        package.HasIndex(x => new { x.MarketingConnectionId, x.CreatedUtc });

        var window = model.Entity<ReceptionistAcceptanceWindow>();
        window.ToTable("ReceptionistAcceptanceWindows", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistAcceptanceWindow_Lifetime", "[StartsUtc] < [ExpiresUtc]");
        });
        window.HasKey(x => x.Id);
        window.HasOne<ReceptionistConfigurationVersion>().WithMany().HasForeignKey(x => new { x.MarketingConnectionId, x.ConfigurationVersionId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Text(window, nameof(ReceptionistAcceptanceWindow.ConfigurationSha256), 64);
        Text(window, nameof(ReceptionistAcceptanceWindow.IngressKey), 200);
        Text(window, nameof(ReceptionistAcceptanceWindow.ProviderProjectKey), 200);
        Text(window, nameof(ReceptionistAcceptanceWindow.RestrictedRouteEvidenceSha256), 64);
        Text(window, nameof(ReceptionistAcceptanceWindow.CreatedByActorId), 450);
        Text(window, nameof(ReceptionistAcceptanceWindow.ReviewedByActorId), 450);
        window.Property(x => x.Revision).IsConcurrencyToken();
        window.HasIndex(x => new { x.MarketingConnectionId, x.ConfigurationVersionId, x.ExpiresUtc });
    }
}
