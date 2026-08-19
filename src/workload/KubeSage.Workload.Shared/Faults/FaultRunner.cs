using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KubeSage.Workload.Shared.Faults;

// Applies the faults that act on the process itself rather than on an
// individual request: crashing after a delay, and exhausting memory.
//
// Both are started in the background so the service comes up normally first.
// That matters for realism: a pod that fails during start-up produces very
// different Kubernetes evidence from one that serves traffic for a while and
// then dies, and the second is the more interesting case to investigate.
public sealed class FaultRunner : BackgroundService
{
    private readonly FaultSettings _faults;
    private readonly ILogger<FaultRunner> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public FaultRunner(FaultSettings faults, ILogger<FaultRunner> logger, IHostApplicationLifetime lifetime)
    {
        _faults = faults;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_faults.AnyEnabled)
        {
            return;
        }

        // Logged loudly so that anyone reading the logs can tell an
        // intentional scenario apart from a genuine bug. The investigating
        // agents never see this line as ground truth - it is ordinary log
        // output they may or may not find, exactly like any other evidence.
        _logger.LogWarning(
            "Fault injection is active: crashAfterSeconds={CrashAfterSeconds} latencyMs={LatencyMs} unready={Unready} allocateMb={AllocateMb} errorRate={ErrorRate}",
            _faults.CrashAfterSeconds,
            _faults.LatencyMilliseconds,
            _faults.Unready,
            _faults.AllocateMegabytes,
            _faults.ErrorRate);

        var tasks = new List<Task>();

        if (_faults.CrashAfterSeconds > 0)
        {
            tasks.Add(CrashAfterDelayAsync(stoppingToken));
        }

        if (_faults.AllocateMegabytes > 0)
        {
            tasks.Add(ExhaustMemoryAsync(stoppingToken));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task CrashAfterDelayAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(_faults.CrashAfterSeconds), stoppingToken);

        _logger.LogCritical(
            "Simulated unrecoverable failure after {UptimeSeconds}s; terminating the process",
            _faults.CrashAfterSeconds);

        // Environment.Exit runs shutdown handlers, which would let the process
        // exit cleanly with code 0 and produce no restart evidence worth
        // looking at. A hard failure exit code is what Kubernetes reports as a
        // container failure and what drives CrashLoopBackOff.
        Environment.FailFast("KubeSage simulated application crash");
    }

    private async Task ExhaustMemoryAsync(CancellationToken stoppingToken)
    {
        // Give the service a moment to become ready and serve some traffic, so
        // the kill lands on a healthy running pod.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        _logger.LogCritical(
            "Allocating {AllocateMb}MB of unmanaged memory to exceed the container memory limit",
            _faults.AllocateMegabytes);

        // Unmanaged memory is used on purpose. The .NET garbage collector
        // honours the container memory limit and would throw
        // OutOfMemoryException inside the process, which Kubernetes reports as
        // an ordinary crash. Allocating outside the managed heap and touching
        // every page forces the kernel's OOM killer to terminate the
        // container, which is what produces a genuine OOMKilled status.
        const int chunkMegabytes = 16;
        var chunks = new List<nint>();

        try
        {
            for (var allocated = 0; allocated < _faults.AllocateMegabytes; allocated += chunkMegabytes)
            {
                var bytes = chunkMegabytes * 1024 * 1024;
                var block = Marshal.AllocHGlobal(bytes);
                chunks.Add(block);

                // Writing to the memory is what makes the kernel actually back
                // it with physical pages. Merely allocating it would not.
                unsafe
                {
                    var span = new Span<byte>((void*)block, bytes);
                    span.Fill(0x5A);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogCritical(ex, "Memory exhaustion fault failed before the container was terminated");
        }
    }
}
