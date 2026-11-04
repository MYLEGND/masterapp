using Microsoft.AspNetCore.Mvc.ApplicationParts;
using ParfaitApp.Services;

namespace AgentPortal.Mobile;

public static class MobileCommerceRegistration
{
    public static IMvcBuilder AddMobileCommerceAuthority(this IMvcBuilder mvc)
    {
        mvc.Services.AddScoped<ParfaitOrderService>();
        return mvc.ConfigureApplicationPartManager(parts =>
        {
            // Service sharing must not publish storefront routes in the Portal host.
            foreach (var part in parts.ApplicationParts.OfType<AssemblyPart>()
                .Where(part => part.Assembly == typeof(ParfaitOrderService).Assembly).ToArray())
                parts.ApplicationParts.Remove(part);
        });
    }
}
