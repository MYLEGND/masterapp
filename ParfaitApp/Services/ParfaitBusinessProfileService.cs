using ParfaitApp.Models;

namespace ParfaitApp.Services;

public interface IParfaitBusinessProfileService
{
    Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default);
}

public sealed class ParfaitBusinessProfileService : IParfaitBusinessProfileService
{
    private static ParfaitBusinessProfileViewModel _profile = new();

    public Task<ParfaitBusinessProfileViewModel> GetProfileAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_profile);
    }

    public Task SaveProfileAsync(ParfaitBusinessProfileViewModel model, CancellationToken ct = default)
    {
        _profile = new ParfaitBusinessProfileViewModel
        {
            StoreName = model.StoreName.Trim(),
            BusinessType = model.BusinessType.Trim(),
            GlobalStoreCheckoutUrl = string.IsNullOrWhiteSpace(model.GlobalStoreCheckoutUrl) ? null : model.GlobalStoreCheckoutUrl.Trim(),
            MetaPixelId = string.IsNullOrWhiteSpace(model.MetaPixelId) ? null : model.MetaPixelId.Trim(),
            MetaTestEventCode = string.IsNullOrWhiteSpace(model.MetaTestEventCode) ? null : model.MetaTestEventCode.Trim()
        };

        return Task.CompletedTask;
    }
}
