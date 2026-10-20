using Infrastructure.Data;
using Infrastructure.Reception;

namespace Infrastructure.Analytics;

public sealed partial class MarketingConnectionStore
{
    // The operator supplies only a reviewed package identity. These commands retain the existing
    // settings transaction, owner lock order and immutable configuration-version authority.
    internal async Task ApplyReceptionistOperatorAsync(ReceptionistOperatorPackage package,
        ReceptionistOperatorState state, ReceptionistProviderBinding? preparedBinding, string actor,
        Func<MasterAppDbContext, Task> guard, CancellationToken ct)
    {
        switch (package.Operation)
        {
            case "accept_binding":
                await AcceptReceptionistBindingCoreAsync(state.Owner, package.Provider,
                    package.ExpectedConnectionRevision, preparedBinding ?? package.Binding!, actor, guard, ct);
                break;
            case "set_owner_admission":
                await SetReceptionistAdmissionCoreAsync(state.Owner, package.Provider,
                    package.ExpectedConnectionRevision, package.Enabled!.Value, guard, ct);
                break;
            default:
                throw new ArgumentException("Unsupported settings operation.");
        }
    }
}
