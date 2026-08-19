using System.Globalization;

namespace KubeSage.Workload.Shared.Faults;

// Controls the deliberate failures the demo workload can be asked to produce.
//
// Every fault is read from an environment variable at start-up. That is a
// deliberate choice: a scenario is activated by patching the Kubernetes
// deployment, which triggers a normal rolling update. This means
//
//   * the change is visible in the cluster as a real deployment event,
//   * the "reset" operation is simply removing the variable again,
//   * and no service has an unauthenticated endpoint that can break it.
//
// The alternative, a /control/break HTTP endpoint, would have been easier but
// would put a destructive unauthenticated route into every service.
public sealed record FaultSettings
{
    public const string CrashAfterSecondsVariable = "KUBESAGE_FAULT_CRASH_AFTER_SECONDS";
    public const string LatencyMillisecondsVariable = "KUBESAGE_FAULT_LATENCY_MS";
    public const string UnreadyVariable = "KUBESAGE_FAULT_UNREADY";
    public const string AllocateMegabytesVariable = "KUBESAGE_FAULT_ALLOCATE_MB";
    public const string ErrorRateVariable = "KUBESAGE_FAULT_ERROR_RATE";

    // Scenario "application crash": the process exits with a failure code a
    // few seconds after starting. Kubernetes restarts it, and after several
    // rounds the pod enters CrashLoopBackOff.
    public int CrashAfterSeconds { get; init; }

    // Scenario "payment latency": the service pauses before answering,
    // pushing callers past their timeout.
    public int LatencyMilliseconds { get; init; }

    // Scenario "readiness failure": the readiness probe starts failing, so
    // Kubernetes takes the pod out of the Service endpoints while leaving the
    // process running.
    public bool Unready { get; init; }

    // Scenario "OOMKilled": the process allocates this much native memory,
    // exceeding the container memory limit so the kernel terminates it.
    public int AllocateMegabytes { get; init; }

    // Fraction of requests answered with HTTP 500, between 0 and 1.
    public double ErrorRate { get; init; }

    public bool AnyEnabled =>
        CrashAfterSeconds > 0 || LatencyMilliseconds > 0 || Unready || AllocateMegabytes > 0 || ErrorRate > 0;

    public static FaultSettings FromEnvironment() => new()
    {
        CrashAfterSeconds = ReadInt(CrashAfterSecondsVariable),
        LatencyMilliseconds = ReadInt(LatencyMillisecondsVariable),
        Unready = ReadBool(UnreadyVariable),
        AllocateMegabytes = ReadInt(AllocateMegabytesVariable),
        ErrorRate = ReadDouble(ErrorRateVariable)
    };

    // An unparsable value means "no fault" rather than a crash at start-up.
    // A typo in a scenario definition should leave the workload healthy, not
    // break it in a way that looks like a real incident.
    private static int ReadInt(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    private static double ReadDouble(string name) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
        && value is > 0 and <= 1
            ? value
            : 0;

    private static bool ReadBool(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value;
}
