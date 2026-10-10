using System.Threading;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalAnalyticsCacheStamp
{
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);
}
