using AgentPortal.Controllers;
using Infrastructure.Bookings;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Services;

/// <summary>Fresh deterministic scope per command/readback, without impersonating a human principal.</summary>
public sealed class ReceptionistCalendarBookingGateway(IServiceScopeFactory scopes) : IReceptionistBookingGateway
{
    private static CalendarController Controller(IServiceProvider services, CancellationToken ct)
    {
        var controller = services.GetRequiredService<CalendarController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services, RequestAborted = ct } };
        return controller;
    }
    public async Task<IReadOnlyList<ReceptionistCalendarSlot>> SlotsAsync(ReceptionistTrustedBookingTarget target,
        DateOnly date, int minimumNoticeMinutes, int maximumAdvanceDays, CancellationToken ct, Func<DateTime, bool>? eligibleStart = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Controller(scope.ServiceProvider, ct).ReadReceptionistSlotsAsync(target, date, minimumNoticeMinutes, maximumAdvanceDays, ct, eligibleStart);
    }
    public async Task CreateAsync(ReceptionistTrustedBookingCommit command, IReceptionistBookingExecutionFence fence, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await Controller(scope.ServiceProvider, ct).CreateReceptionistAppointmentAsync(command, fence, ct);
    }
    public async Task<bool> VerifyAsync(ReceptionistTrustedBookingCommit command, ReceptionistProviderBookingIdentity identity, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Controller(scope.ServiceProvider, ct).VerifyReceptionistAppointmentAsync(command, identity, ct);
    }
    public async Task<ReceptionistCalendarSlot?> ReadOriginalSlotAsync(ReceptionistTrustedBookingTarget target, string originalAppointmentId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Controller(scope.ServiceProvider, ct).ReadOriginalReceptionistSlotAsync(target, originalAppointmentId, ct);
    }
}
