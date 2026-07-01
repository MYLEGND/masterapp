using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitMetaAdsConnectionStoreAdapter : IMetaAdsConnectionStore
{
    public const string SiteKey = "ParfaitApp";

    private static readonly Guid SiteScopeId = MetaAdsScopeKey.ForSite(SiteKey);

    private readonly IParfaitBusinessProfileService _businessProfile;

    public ParfaitMetaAdsConnectionStoreAdapter(IParfaitBusinessProfileService businessProfile)
    {
        _businessProfile = businessProfile;
    }

    public async Task<MetaAdsConnectionRecord?> GetAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        if (agentTrackingProfileId != SiteScopeId)
            return null;

        var connection = await _businessProfile.GetMetaConnectionRecordAsync(ct);
        if (connection is null || string.IsNullOrWhiteSpace(connection.AccessToken))
            return null;

        return new MetaAdsConnectionRecord
        {
            AgentTrackingProfileId = SiteScopeId,
            AccessToken = connection.AccessToken,
            AccessTokenExpiresUtc = connection.AccessTokenExpiresUtc,
            AccountId = connection.AccountId,
            AccountName = connection.AccountName,
            BusinessId = connection.BusinessId,
            BusinessName = connection.BusinessName,
            MetaUserId = connection.MetaUserId,
            MetaUserName = connection.MetaUserName,
            ConnectedUtc = connection.ConnectedUtc,
            UpdatedUtc = connection.UpdatedUtc
        };
    }

    public Task SaveAsync(MetaAdsConnectionRecord record, CancellationToken ct = default)
    {
        if (record.AgentTrackingProfileId != SiteScopeId)
            return Task.CompletedTask;

        return _businessProfile.SaveMetaConnectionAsync(new ParfaitMetaAdsConnectionRecord
        {
            AccessToken = record.AccessToken,
            AccessTokenExpiresUtc = record.AccessTokenExpiresUtc,
            AccountId = record.AccountId,
            AccountName = record.AccountName,
            BusinessId = record.BusinessId,
            BusinessName = record.BusinessName,
            MetaUserId = record.MetaUserId,
            MetaUserName = record.MetaUserName,
            ConnectedUtc = record.ConnectedUtc,
            UpdatedUtc = record.UpdatedUtc
        }, ct);
    }

    public Task DeleteAsync(Guid agentTrackingProfileId, CancellationToken ct = default)
    {
        if (agentTrackingProfileId != SiteScopeId)
            return Task.CompletedTask;

        return _businessProfile.DisconnectMetaAsync(ct);
    }
}
