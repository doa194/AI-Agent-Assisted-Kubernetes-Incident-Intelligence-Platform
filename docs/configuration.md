# Configuration

Every tunable value, what it does, and what happens if you change it.

- [Where configuration lives](#where-configuration-lives)
- [How to override a setting](#how-to-override-a-setting)
- [Validation](#validation)
- [Database](#database)
- [Ollama](#ollama)
- [Telemetry](#telemetry)
- [Kubernetes](#kubernetes)
- [Detection](#detection)
- [Analysis](#analysis)
- [Investigation](#investigation)
- [Retrieval](#retrieval)
- [Pinned versions](#pinned-versions)
- [Tuning for different hardware](#tuning-for-different-hardware)

---

## Where configuration lives

| Concern | File |
| --- | --- |
| Platform behaviour | `src/KubeSage.Platform/appsettings.json`, `KubeSage` section |
| Defaults in code | `src/KubeSage.Platform/Configuration/KubeSageOptions.cs` |
| Container images and models | `versions.env` |
| NuGet package versions | `Directory.Packages.props` |
| Deployment wiring | `deploy/compose/docker-compose.yml` |
| Cluster shape and ports | `deploy/kind/cluster.yaml` |

`versions.env` is the single source of truth for every external version. Both
Docker Compose (via `--env-file`) and the Python automation read it, so a
version is written down exactly once.

---

## How to override a setting

Environment variables use the standard .NET convention — section names separated
by double underscores:

```yaml
environment:
  KubeSage__Detection__EvaluationIntervalSeconds: "30"
  KubeSage__Investigation__MaxConcurrent: "2"
  KubeSage__Ollama__ContextTokens: "16384"
```

Or edit `appsettings.json` directly and rebuild the platform image.

Configuration is read **once at startup**. There is no live reload; changes need
a restart.

---

## Validation

Every setting is validated at startup. The process refuses to start on invalid
configuration rather than failing later mid-investigation.

Two layers:

**Per-field ranges** via `[Range]` attributes — a threshold outside its sensible
bounds is rejected.

**Cross-field relationships**, which are the ones that actually hurt: each value
looks reasonable alone but the combination misbehaves in a way that is hard to
spot later.

| Rule | What breaks without it |
| --- | --- |
| `Investigation:WorkLeaseSeconds` ≥ `Investigation:TimeoutSeconds` | A running investigation can be claimed twice → duplicate reports |
| `Detection:DeduplicationCooldownMinutes` ≥ `Detection:EvaluationWindowMinutes` | The same condition raises an incident on every pass |
| `Detection:RecoveryConfirmationMinutes` ≥ `Detection:EvaluationWindowMinutes` | Incidents flap open and closed |
| `Detection:EvaluationWindowMinutes` ≤ `Telemetry:MaxQueryRangeMinutes` | Rules evaluate truncated data as if it were complete |
| `Investigation:TimeoutSeconds` ≥ `Ollama:RequestTimeoutSeconds` | No investigation can ever finish |
| `Kubernetes:AllowedNamespaces` contains `Telemetry:WorkloadNamespace` | Evidence collection is blocked for the observed workload |
| Endpoints must be absolute `http`/`https` URIs | Confusing failures at first use |

A failure looks like this, naming both values and the consequence:

```
Investigation.WorkLeaseSeconds (900) must be at least
Investigation.TimeoutSeconds (1800); a shorter lease allows a running
investigation to be claimed twice.
```

---

## Database

| Setting | Default | Notes |
| --- | --- | --- |
| `ConnectionString` | — | **Required.** Should point at the low-privilege `kubesage_app` role |
| `MigrationConnectionString` | falls back to `ConnectionString` | The schema-owner role, used only at startup |
| `MaxPoolSize` | 20 | Npgsql connection pool ceiling |
| `CommandTimeoutSeconds` | 30 | Per-command timeout |
| `RunMigrationsOnStartup` | `true` | Disable only if migrations are applied externally |

Two connection strings so day-to-day queries never run with schema-owner rights.
The application role can read and write rows but cannot create, alter or drop
tables — verified by `verify` actually attempting a `CREATE TABLE`.

---

## Ollama

| Setting | Default | Notes |
| --- | --- | --- |
| `Endpoint` | `http://localhost:11434` | `http://ollama:11434` inside Compose |
| `ChatModel` | `gemma4:12b` | Used by all four agents |
| `EmbeddingModel` | `embeddinggemma:300m` | 768 dimensions |
| `ContextTokens` | 8192 | See below — this is not free |
| `RequestTimeoutSeconds` | 900 | Catches a hung server, not slowness |
| `Temperature` | 0.1 | Low: reproducibility matters more than variety |
| `EmbeddingDimensions` | 768 | **Must match the database column** |
| `StartupProbeTimeoutSeconds` | 30 | How long to wait for the model at startup |

### `ContextTokens` is a memory decision, not just a size

The key/value cache is allocated up front and competes with model weights for
video memory. Raising this pushes model layers onto the CPU.

Measured on a GTX 1060 6GB: 16384 → 8192 was part of taking triage from a
600-second timeout down to 106 seconds.

**Trim evidence to fit the window rather than growing the window to fit the
evidence.** That is what `Investigation:MaxEvidenceItems` is for.

### `EmbeddingDimensions` is a schema contract

The `semantic_memory.embedding` column is `vector(768)`. Changing the embedding
model requires a **migration and a full re-index**, not just a configuration
change — vectors from two different models are not comparable. The
`EmbeddingClient` validates the returned dimension on every call and fails with
an explanation rather than letting a mismatch surface later as a confusing
insert error.

### `RequestTimeoutSeconds` is generous on purpose

A 12B model on modest hardware legitimately takes minutes for one structured
answer. This exists to catch a hung model server, not to enforce
responsiveness. Cutting it short turns a slow success into a failure.

---

## Telemetry

| Setting | Default | Notes |
| --- | --- | --- |
| `LokiEndpoint` | `http://localhost:3100` | `host.docker.internal:3100` inside Compose |
| `PrometheusEndpoint` | `http://localhost:9090` | `host.docker.internal:9090` inside Compose |
| `QueryTimeoutSeconds` | 30 | Per-query timeout |
| `MaxLogLinesPerQuery` | 500 | Hard ceiling on any single log query |
| `MaxQueryRangeMinutes` | 120 | Hard ceiling on how far back any query may look |
| `WorkloadNamespace` | `kubesage-demo` | The observed namespace |

`MaxLogLinesPerQuery` and `MaxQueryRangeMinutes` are safety limits for the whole
platform. They protect Loki from an expensive query and protect the model's
context from being flooded by an agent asking for too much at once.

Over-large requests are **clamped, not rejected** — and clamping trims from the
*start* of the window, keeping the end. During an incident the most recent data
is what matters, so if something must be dropped it should be the oldest.

---

## Kubernetes

| Setting | Default | Notes |
| --- | --- | --- |
| `KubeConfigPath` | `null` | Empty uses the default resolution order |
| `AllowedNamespaces` | none — must be configured | The containment boundary |
| `RequestTimeoutSeconds` | 30 | Per-request timeout |
| `MaxItemsPerQuery` | 200 | Ceiling on any single list call |

`AllowedNamespaces` is a real boundary, not a convenience. A namespace outside
the list is refused **before any request leaves the process**, so a confused or
manipulated agent cannot browse the cluster. This is layer two of three; RBAC is
the third.

**It is the one setting with no built-in default.** `appsettings.json` ships
`["kubesage-demo", "kubesage-observability"]`, and that file is the only source.
The reason is a sharp edge in the configuration binder: it *adds to* an array
that already holds values rather than replacing it, so a default in code could
be widened by configuration but never narrowed. An operator removing a namespace
would see the platform start cleanly while the namespace stayed readable.

Leaving it unset is therefore a start-up failure rather than a fallback:

```
Kubernetes.AllowedNamespaces: The field AllowedNamespaces must be a string or
array type with a minimum length of '1'.
```

`KubeConfigPath` is set to the generated read-only kubeconfig inside Compose.
Left empty during local development it picks up your own kubeconfig — convenient,
but note that means your real credentials, so tests point it at a dead endpoint
deliberately.

---

## Detection

| Setting | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | Master switch |
| `EvaluationWindowMinutes` | 5 | The sliding window each rule evaluates |
| `EvaluationIntervalSeconds` | 60 | How often the loop runs |
| `DeduplicationCooldownMinutes` | 15 | How long a fingerprint stays suppressed |
| `RecoveryConfirmationMinutes` | 10 | Absence required before marking recovered |

### Thresholds

| Threshold | Default | Fires when |
| --- | --- | --- |
| `HttpErrorRate` | 0.10 | 5xx share exceeds 10% |
| `MinimumRequestSample` | 20 | Below this, ratios are ignored entirely |
| `LatencyP95Seconds` | 1.5 | p95 request duration exceeds this |
| `PodRestartIncrease` | 2 | Restarts *increase* by this much in the window |
| `UnreadyPodCount` | 1 | This many pods unready without restarting |
| `RepeatedErrorSignatureCount` | 10 | One normalised error repeats this often |
| `DependencyFailureCount` | 5 | Failures calling a named dependency |

**`MinimumRequestSample` matters more than it looks.** Without it, one failure
out of two requests is a 50% error rate and pages someone at 3am over nothing.

**`PodRestartIncrease` is an increase, not a total.** A pod that crash-looped
last week still carries those restarts; using the absolute count would raise an
incident every minute forever. Previous counts are persisted in
`detection_state` so the comparison survives a platform restart.

### Tuning guidance

| Want | Change |
| --- | --- |
| Faster detection | Lower `EvaluationIntervalSeconds` (costs more queries) |
| Fewer false positives on bursty traffic | Raise `MinimumRequestSample` |
| Catch briefer incidents | Lower `EvaluationWindowMinutes` — but also lower the cooldown and recovery values, or validation refuses |
| Fewer duplicate incidents | Raise `DeduplicationCooldownMinutes` |
| Faster recovery marking | Lower `RecoveryConfirmationMinutes`, but not below the window |

---

## Analysis

| Setting | Default | Notes |
| --- | --- | --- |
| `RunStartupAnalysis` | `true` | Produce a cluster report shortly after start |
| `StartupWarmupSeconds` | 120 | Let telemetry accumulate first |
| `RunScheduledAnalysis` | `true` | Periodic cluster health report |
| `ScheduledIntervalSeconds` | 300 | Every five minutes |

The warm-up exists because Loki and Prometheus have almost no data immediately
after the cluster starts. Producing a report straight away would describe an
empty system and say nothing useful.

---

## Investigation

| Setting | Default | Notes |
| --- | --- | --- |
| `MaxConcurrent` | 1 | Parallel investigations |
| `TimeoutSeconds` | 1800 | Total budget across all three agents |
| `MaxToolCalls` | 20 | Tool budget per investigation |
| `MaxEvidenceItems` | 60 | Evidence items given to a model |
| `MaxRetries` | 3 | Before an item is left `Failed` |
| `RetryBaseDelaySeconds` | 30 | Exponential: 30s, 60s, 120s… capped at 900s |
| `DispatcherPollSeconds` | 5 | How often the queue is polled |
| `WorkLeaseSeconds` | 2400 | How long claimed work stays claimed |

### `MaxConcurrent` defaults to 1 for a measured reason

A local 12B model does not gain throughput from parallel investigations — it
loses to memory pressure and timeouts. On real inference capacity this should
rise; the work queue already supports it without change.

### `MaxEvidenceItems` prevents a real failure

An investigation with 113 evidence items produced a hypothesis trying to cite 44
of them, ran past the output token limit, and returned JSON truncated mid-array
— nine minutes of model time wasted on an unparsable answer.

Items are selected by `EvidenceSelector` in priority order: cluster state, then
Kubernetes events, then metrics, then log signatures, then history and runbooks,
then individual log lines. The order reflects information per item, which is
roughly the inverse of how many of each kind exist.

### `WorkLeaseSeconds` must exceed `TimeoutSeconds`

The margin covers the gap between an investigation hitting its own budget and
the worker finishing clean-up. Without it a still-running investigation could be
claimed a second time and produce a duplicate report. Validation enforces this.

Long investigations renew their lease in the background at roughly a third of
the interval, so two renewals can fail before work is considered abandoned.

---

## Retrieval

| Setting | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | Disabling leaves investigations on live telemetry only |
| `TopK` | 5 | Maximum matches returned |
| `MaxDistance` | 0.65 | Cosine distance beyond which a match is discarded |
| `IndexRunbooksOnStartup` | `true` | Re-index changed runbook sections at start |

**`MaxDistance` is a quality gate, not a performance one.** Results beyond it are
dropped even if that means returning fewer than `TopK`. Returning a weak match
is worse than returning nothing: an agent handed an unrelated past incident will
try to make it fit.

Raising it toward 1.0 returns more, less relevant, results. The gold-set
evaluation (`python kubesage.py verify`) is how to tell whether a change helped
— it scores whether the *right* document is retrieved, not just some document.

Runbook indexing skips sections whose content hash is unchanged, so a normal
restart costs nothing.

---

## Pinned versions

From `versions.env`:

```bash
OLLAMA_IMAGE=ollama/ollama:0.32.13
POSTGRES_IMAGE=pgvector/pgvector:pg18-trixie
GRAFANA_IMAGE=grafana/grafana:13.1.3
LOKI_IMAGE=grafana/loki:3.7.6
PROMETHEUS_IMAGE=prom/prometheus:v3.13.2
FLUENTBIT_IMAGE=fluent/fluent-bit:4.2.8
KIND_NODE_IMAGE=kindest/node:v1.36.1@sha256:3489c767...

KUBESAGE_CHAT_MODEL=gemma4:12b
KUBESAGE_EMBEDDING_MODEL=embeddinggemma:300m
```

### Host ports

| Variable | Default | Serves |
| --- | --- | --- |
| `PLATFORM_HOST_PORT` | 8081 | KubeSage API |
| `GRAFANA_HOST_PORT` | 3000 | Grafana |
| `OLLAMA_HOST_PORT` | 11434 | Ollama |
| `POSTGRES_HOST_PORT` | 5433 | PostgreSQL (5433 to avoid a local install) |
| `KIND_API_HOST_PORT` | 6443 | Kubernetes API |
| `LOKI_HOST_PORT` | 3100 | Loki |
| `PROMETHEUS_HOST_PORT` | 9090 | Prometheus |
| `GATEWAY_HOST_PORT` | 8080 | Demo application |

Changing a port here changes it everywhere — Compose, the platform's
configuration, Grafana's datasources and the automation all read this file.
The Kind node ports in `deploy/kind/cluster.yaml` are the one exception and must
be kept in step manually.

### Ollama environment

Set in `deploy/compose/docker-compose.yml`, and consequential:

| Variable | Value | Why |
| --- | --- | --- |
| `OLLAMA_MAX_LOADED_MODELS` | 1 | See below |
| `OLLAMA_NUM_PARALLEL` | 1 | Parallel requests cause memory pressure, not throughput |
| `OLLAMA_KEEP_ALIVE` | 30m | Avoids reloading between investigations |
| `OLLAMA_MAX_QUEUE` | 64 | With one request served at a time, queuing beyond this is rejected rather than left to time out |
| `OLLAMA_HOST` | 0.0.0.0:11434 | Listens on all interfaces so the published port works |

**`OLLAMA_MAX_LOADED_MODELS=1` is counter-intuitive and was measured.** Keeping
the embedding model resident alongside the chat model on a 6 GB card pushes chat
layers onto the CPU. The figures that matter are the **resident** footprints
reported by `ollama ps` — 8.1 GB and 681 MB — not the 7.6 GB and 621 MB shown on
disk by `ollama list`. Prompt processing collapsed to 14
tokens/sec — slow enough that a triage call exceeded a 600-second timeout.

Reloading costs ~34 s once per investigation. Losing GPU residency costs minutes
on *every* call.

The trade-off: an embed request issued while a generation is running waits for
that generation *plus* a ~58 s model swap. That is why embedding timeouts are
300 s rather than 120 s.

---

## Tuning for different hardware

### More GPU memory (12 GB+)

The whole model fits in video memory, so several defaults become conservative:

```yaml
KubeSage__Ollama__ContextTokens: "16384"
KubeSage__Investigation__MaxEvidenceItems: "100"
KubeSage__Investigation__MaxConcurrent: "2"
```

With enough headroom, `OLLAMA_MAX_LOADED_MODELS=2` also becomes worthwhile —
the reason for 1 is contention on a small card, which no longer applies.

### No GPU

Everything works, just slower. Raise the timeouts so slow successes are not
turned into failures:

```yaml
KubeSage__Ollama__RequestTimeoutSeconds: "1800"
KubeSage__Investigation__TimeoutSeconds: "3600"
KubeSage__Investigation__WorkLeaseSeconds: "4200"
```

Remember the validated relationship: lease ≥ timeout ≥ model request timeout.

### Less Docker memory (8 GB)

Reduce what competes with the model:

- lower the workload replica counts in `deploy/k8s/workload/`
- lower Prometheus retention (`--storage.tsdb.retention.time`)
- set `KubeSage__Ollama__ContextTokens: "4096"`

Preflight warns below 12 GB rather than refusing, so you can try.

### A quieter or noisier demo

Traffic rate is set on the traffic generator deployment:

```yaml
- name: Traffic__RequestsPerMinute
  value: "30"     # ~1 request every 2 seconds
```

Below about 20/minute, `MinimumRequestSample` starts suppressing error-rate
detection within a five-minute window — which is correct behaviour, but means
that rule stops contributing.
