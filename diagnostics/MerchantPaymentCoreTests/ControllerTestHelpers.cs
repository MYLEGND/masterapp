using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace AgentPortal.Tests;
// Same nonmonetary database fixture used by the linked tests, without importing
// their unrelated portal controller/host graph into this focused executable.
internal static class ControllerTestHelpers
{
    public static MasterAppDbContext BuildDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptors)
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
}
