# Codebase guide

Where things live, and how to change them. Read
[architecture.md](architecture.md) first for *why* the structure looks like this.

- [Repository layout](#repository-layout)
- [Where to look for a given question](#where-to-look-for-a-given-question)
- [Reading paths](#reading-paths)
- [How to add things](#how-to-add-things)
- [Conventions](#conventions)
- [Sharp edges](#sharp-edges)

---

## Repository layout

```
.
├── kubesage.py                    launcher — every command starts here
├── versions.env                   every pinned container image and model
├── KubeSage.slnx                  solution file
├── Directory.Packages.props       every pinned NuGet version
├── Directory.Build.props          shared MSBuild settings
├── global.json                    .NET SDK pin
│
├── src/
│   ├── KubeSage.Platform/         the AI platform (modular monolith)
│   └── workload/                  the observed demo application
│       ├── KubeSage.Workload.Shared/            logging, correlation, faults, metrics
│       ├── KubeSage.Workload.Gateway/
│       ├── KubeSage.Workload.OrderApi/
│       ├── KubeSage.Workload.PaymentSimulator/
│       ├── KubeSage.Workload.NotificationWorker/
│       ├── KubeSage.Workload.TrafficGenerator/
│       └── Dockerfile                            one file builds all five
│
├── automation/kubesage/           all Python automation (stdlib only)
│   └── scenarios/                 failure definitions + private ground truth
│
├── deploy/
│   ├── kind/cluster.yaml          three nodes, fixed host port mappings
│   ├── compose/                   operations plane + Grafana provisioning
│   └── k8s/
│       ├── workload/              the demo application
│       ├── observability/         Loki, Fluent Bit, Prometheus
│       └── rbac/                  the read-only observer identity
│
├── knowledge/runbooks/            five runbooks, embedded into the assembly
├── tests/                         unit, integration, API
└── docs/
```

---

## Where to look for a given question

| Question | File |
| --- | --- |
| What settings exist and what are the defaults? | `Configuration/KubeSageOptions.cs` |
| Why did the platform refuse to start? | `Configuration/KubeSageOptionsValidator.cs` |
| How is a log line turned into evidence? | `Modules/Telemetry/LokiClient.cs` |
| How is a metric turned into evidence? | `Modules/Telemetry/PrometheusClient.cs` |
| What stops a hostile workload name reaching a query? | `Modules/Telemetry/TelemetryQuery.cs` |
| What gets redacted before a model sees it? | `Modules/Telemetry/SensitiveDataRedactor.cs` |
| How are repeated log lines collapsed? | `Modules/Telemetry/LogSignature.cs` |
| What can the platform read from Kubernetes? | `Modules/Kubernetes/KubernetesEvidenceClient.cs` + `deploy/k8s/rbac/` |
| When does an incident get raised? | `Modules/Detection/DetectionRules.cs` |
| Why did one outage not become twelve incidents? | `Modules/Detection/CandidateSuppression.cs` |
| How is a duplicate incident recognised? | `Modules/Incidents/IncidentFingerprint.cs` |
| What states can an incident be in? | `Modules/Incidents/IncidentState.cs` |
| How does work survive a restart? | `Modules/Persistence/WorkQueue.cs` + `AgentWorkflows/StartupRecoveryService.cs` |
| What are the agents told? | `Modules/AgentWorkflows/PromptBuilder.cs` |
| What shape must an agent answer in? | `Modules/AgentWorkflows/AgentContracts.cs` |
| How is a fabricated citation caught? | `Modules/AgentWorkflows/AgentOutputValidator.cs` |
| What can an agent actually do? | `Modules/AgentWorkflows/InvestigationTools.cs` |
| How do the three agents run in order? | `Modules/AgentWorkflows/InvestigationWorkflow.cs` |
| How is Ollama driven? | `Modules/AgentWorkflows/OllamaChatClientAdapter.cs` |
| How is semantic search done? | `Modules/Retrieval/SemanticMemoryRepository.cs` |
| What is the database shape? | `Modules/Persistence/Migrations/*.sql` |
| What does the API expose? | `Api/*.cs` |
| How do I run anything? | `automation/kubesage/cli.py` |
| What does a scenario actually change? | `automation/kubesage/scenarios/definitions.py` |

---

## Reading paths

Rather than reading the platform's 48 source files in alphabetical order, follow
one thread.

### "How does an incident become a report?"

1. `Modules/Detection/DetectionEngine.cs` — `RunPassAsync` is the whole loop
2. `Modules/Detection/DetectionRules.cs` — what makes a rule fire
3. `Modules/Incidents/IncidentRepository.cs` — `RecordCandidateAsync` decides
   new versus duplicate
4. `Modules/Persistence/WorkQueue.cs` — `EnqueueAsync`, then `ClaimAsync`
5. `Modules/AgentWorkflows/InvestigationDispatcher.cs` — picks the work up
6. `Modules/AgentWorkflows/InvestigationWorkflow.cs` — `BuildWorkflow` is the graph
7. `Modules/AgentWorkflows/AgentOutputValidator.cs` — where claims are checked
8. `Modules/Reporting/ReportRepository.cs` — what gets stored

### "How is the model prevented from making things up?"

1. `Modules/Telemetry/Evidence.cs` — deterministic identifiers
2. `Modules/AgentWorkflows/AgentContracts.cs` — schemas requiring `evidenceIds`
3. `Modules/AgentWorkflows/PromptBuilder.cs` — the untrusted-data boundary
4. `Modules/AgentWorkflows/AgentOutputValidator.cs` — the enforcement

### "How is the cluster protected?"

1. `deploy/k8s/rbac/kubesage-observer.yaml` — what the identity may do
2. `Modules/AgentWorkflows/InvestigationTools.cs` — the allow-list
3. `Modules/Telemetry/TelemetryQuery.cs` — input validation and clamping
4. `automation/kubesage/rbac.py` — `verify_read_only` asks the API server itself

### "What does the demo workload actually do?"

1. `src/workload/KubeSage.Workload.Shared/WorkloadDefaults.cs` — everything shared
2. `src/workload/KubeSage.Workload.OrderApi/Program.cs` — the two-dependency service
3. `src/workload/KubeSage.Workload.Shared/Faults/FaultSettings.cs` — how failures inject

---

## How to add things

### A detection rule

1. Implement `IDetectionRule` in `Modules/Detection/DetectionRules.cs`:

```csharp
public sealed class MyRule : IDetectionRule
{
    public string Name => "my-rule";

    public IEnumerable<IncidentCandidate> Evaluate(
        DetectionSnapshot snapshot, DetectionOptions options)
    {
        // pure function of the snapshot — no I/O, no model
        yield return new IncidentCandidate { /* ... */ };
    }
}
```

2. Register it in `DetectionModule.AddDetection`:

```csharp
services.AddSingleton<IDetectionRule, MyRule>();
```

3. If it needs a threshold, add it to `DetectionThresholds` with a `[Range]`.
4. If it introduces a new category, add a constant to `IncidentCategory` and
   consider its position in `CandidateSuppression.Precedence`.
5. Unit test it. Rules are pure functions, so this is cheap — construct a
   snapshot, evaluate, assert.

**Do not** perform I/O in a rule. Add what you need to `DetectionSnapshot` and
populate it in `DetectionEngine.BuildSnapshotAsync`, so the rule stays testable
and detection keeps working when a source is unavailable.

### An agent tool

1. Add a `ToolDescriptor` to `InvestigationTools.Descriptors`. The description is
   what the model sees, so state the arguments precisely.
2. Add a case to `InvestigationTools.DispatchAsync`.
3. Validate and clamp every argument. Use `_guard.RequireWorkload`,
   `_guard.RequireNamespace`, and `Minutes(call, fallback)`.
4. Return `IReadOnlyList<Evidence>` — never raw text. Evidence gets identifiers,
   and identifiers are what the validator checks.

The tool is wrapped automatically by `InvestigationToolFactory`, so budget
enforcement and evidence accumulation come for free.

**Never add a tool that mutates anything.** The RBAC identity would refuse, but
the allow-list is the first of three layers and should not be the one that fails.

### A migration

1. Create `Modules/Persistence/Migrations/00N_description.sql`. The numeric
   prefix determines order.
2. It is picked up automatically — the csproj globs `Migrations/*.sql` as
   embedded resources.
3. **Never edit an applied migration.** The checksum check refuses it, on
   purpose: an edited script leaves two databases in different shapes while both
   claim to be up to date.

### A workload service

1. Create the project under `src/workload/`, referencing
   `KubeSage.Workload.Shared`.
2. Call `builder.AddWorkloadDefaults("service-name")` and
   `app.UseWorkloadDefaults()`. That gives the shared log format, correlation
   propagation, fault injection, `/metrics`, and health probes.
3. Add it to `SERVICES` in `automation/kubesage/workloads.py`.
4. Add a manifest under `deploy/k8s/workload/` with the
   `prometheus.io/scrape: "true"` annotations and an
   `app.kubernetes.io/name` label matching the service name.

The label matters: it is how Prometheus derives the `workload` label and how the
Kubernetes adapter relates pods to workloads.

### A failure scenario

1. Add a `Scenario` to `automation/kubesage/scenarios/definitions.py`. Prefer
   `set-env` or `scale` — nothing should reach into a running container.
2. Add an `ExpectedOutcome` to `ground_truth.py`, including
   `incorrect_root_cause_workloads` so a report blaming a victim is caught.
3. If it needs a new fault, add it to `FaultSettings` and handle it in
   `FaultRunner` or the relevant service.
4. Verify: `python kubesage.py scenario check <name>`.

Remember `ground_truth.py` is the answer key — it must never reach the platform.
It stays in the Python package, which is never copied into the container image.

### An API endpoint

1. Add it to the relevant file under `Api/`, or create a new
   `Map<Thing>Endpoints` extension.
2. Map it in `Program.cs`.
3. Catch `TelemetryQueryRejectedException` → 400 and
   `TelemetryUnavailableException` → 503. Callers rely on being able to tell a
   bad request from a missing dependency.
4. Add a component test in `tests/KubeSage.Platform.ApiTests/`.

---

## Conventions

**Comments explain *why*, not *what*.** The codebase deliberately documents
reasoning, trade-offs and hard-won findings. If a comment restates the code, it
should be deleted; if a decision would surprise a reader, it should be explained.

**Warnings are errors.** `TreatWarningsAsErrors` is on. A nullability warning is
a build failure.

**No floating versions.** Container images, models and NuGet packages are all
pinned exactly.

**Row types are classes, not positional records.** See [Sharp edges](#sharp-edges).

**Tests assert on behaviour that can fail silently.** Not on line coverage. See
[testing-strategy.md](testing-strategy.md).

**Python uses the standard library only.** No `pip install` before a first
command works. This is a deliberate constraint on the automation.

---

## Sharp edges

Things that have already cost debugging time. Each is commented at the site too.

### Dapper and `timestamptz`

Dapper matches constructors by the reader's column types, and Npgsql returns
`DateTime` for `timestamptz`, not `DateTimeOffset`. A positional record fails to
materialise with an error that names every column and explains none of them.

**All row types are plain classes with a parameterless constructor**, mapping to
`DateTime` and converting once in a `To...()` method.

### pgvector parameters need an explicit type

```csharp
// fails at runtime with an InvalidCastException that never mentions vectors
command.Parameters.AddWithValue("embedding", new Vector(values));

// correct
command.Parameters.Add(new NpgsqlParameter
{
    ParameterName = "embedding", DataTypeName = "vector", Value = new Vector(values)
});
```

### Npgsql caches the type catalogue

It reads the database's types once. If the data source connects before pgvector
exists, it never learns the type. `Program.cs` calls `ReloadTypesAsync` after
migrations for exactly this reason.

### Gemma 4 hides its output

`/api/generate` returns an empty `response` because all tokens go to the
`thinking` channel — indistinguishable from a broken model. Always use
`/api/chat`, and pass `think` explicitly.

### Work payloads are case-insensitive on read

Payloads are written from anonymous objects (C# casing) and read into records
(their own casing). A case-sensitive read produced a default-valued object, so
the dispatcher found no incident id, marked the item `Completed`, and moved on —
a queue draining while doing nothing, indistinguishable from a healthy one.

`WorkItem.PayloadAs<T>` sets `PropertyNameCaseInsensitive`, and an unreadable
payload now throws rather than skipping.

### The configuration binder appends to arrays, it does not replace them

Give an array-valued option a default in C# *and* a value in
`appsettings.json` and you get both, concatenated.

That is merely untidy for most settings and dangerous for
`Kubernetes.AllowedNamespaces`, which is a security boundary: the list could be
widened by configuration but never narrowed, so removing a namespace looked like
it worked and changed nothing.

`AllowedNamespaces` therefore defaults to `[]` in code, with the real list only
in `appsettings.json` and `[Required, MinLength(1)]` turning an absent list into
a start-up failure. Apply the same rule to any array option added later:
**the default lives in configuration, not in the property initialiser.**

### A lambda taking only `HttpContext` is a `RequestDelegate`

Its `IResult` is silently discarded. Cast to
`(Func<HttpContext, Task<IResult>>)` when a handler takes no other parameter.

### `kubectl logs` shows the current container

A crashed container's last words are in the *previous* instance. Use
`--previous` when investigating anything that kills a process.

### Pod status has two reasons, not one

`state.waiting.reason` (CrashLoopBackOff) and
`lastState.terminated.reason` (OOMKilled) answer different questions. Reading
only the first discards the OOM signal, because an OOM-killed container
immediately enters CrashLoopBackOff — destroying exactly the distinction that
decides whether the fix is a memory limit or a code change.

### One model resident at a time

Keeping the embedding model loaded alongside the chat model on a small GPU pushes
chat layers to the CPU and collapses prompt processing. The trade-off is that an
embed request during a generation waits for that generation *plus* a ~58s model
swap, which is why embedding timeouts are 300s.

---

## Building and running locally

```bash
dotnet build                                    # whole solution
dotnet test                                     # all three test projects
dotnet test tests/KubeSage.Platform.UnitTests   # fast, no Docker needed

python kubesage.py workload                     # rebuild the demo services

cd deploy/compose && docker compose --env-file ../../versions.env \
  -f docker-compose.yml -f docker-compose.gpu.yml up -d --build platform
```

Omit `-f docker-compose.gpu.yml` without an NVIDIA GPU.

To run the platform outside a container against the running environment, leave
`Kubernetes:KubeConfigPath` empty so it picks up your kubeconfig, and point the
telemetry endpoints at `127.0.0.1` instead of `host.docker.internal`.
