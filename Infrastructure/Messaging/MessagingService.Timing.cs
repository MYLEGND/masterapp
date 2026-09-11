using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed partial class MessagingService
{
    // Attached to the actual service invocation, never a separate diagnostic read.
    // Only fixed stage names, counts and timings are logged; no content or actor IDs.
    private sealed class ProjectionTiming(ILogger logger, string operation) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _stageStarted = Stopwatch.GetTimestamp();
        private string _stage = "authorization";
        private bool _completed;

        public void Next(string stage)
        {
            RecordStage();
            _stage = stage;
            _stageStarted = Stopwatch.GetTimestamp();
        }

        public void Complete() => _completed = true;

        private void RecordStage() => logger.LogInformation(
            "Messaging projection stage. Operation={Operation} Stage={Stage} ElapsedMs={ElapsedMs} TraceId={TraceId}",
            operation, _stage, Stopwatch.GetElapsedTime(_stageStarted).TotalMilliseconds,
            Activity.Current?.TraceId.ToString());

        public void Dispose()
        {
            RecordStage();
            logger.LogInformation(
                "Messaging projection completed. Operation={Operation} Succeeded={Succeeded} ElapsedMs={ElapsedMs} TraceId={TraceId}",
                operation, _completed, Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
        }
    }
}
