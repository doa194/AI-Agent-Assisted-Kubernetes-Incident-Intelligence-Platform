# Observability and the telemetry model

How data gets from a running container into evidence an agent can cite.

- [Where data lives](#where-data-lives)
- [The log contract](#the-log-contract)
- [Loki labels](#loki-labels)
- [The Fluent Bit pipeline](#the-fluent-bit-pipeline)
- [Metrics](#metrics)
- [Kubernetes state](#kubernetes-state)
- [Evidence](#evidence)
- [Grafana](#grafana)
- [Verifying without any AI](#verifying-without-any-ai)

---

## Where data lives

Raw operational data stays in the system built for it. The platform queries
those systems when an investigation needs a specific slice, and copies almost
nothing.

| Data | Home | Retention | Copied into PostgreSQL? |
| --- | --- | --- | --- |
| Log lines | Loki | 7 days | Only the slice attached to an incident |
| Metric samples | Prometheus | 6 hours | Never — only computed summaries |
| Cluster state and events | Kubernetes API | Kubernetes default | Only as evidence on an incident |
| Incidents, reports, memory | PostgreSQL | Indefinite | — |

**The one deliberate exception:** evidence supporting an incident is copied into
`incident_evidence`. Loki and Prometheus age their data out, and a report read
next week must still be able to show what it was based on. That copy is bounded
to the slice that supported a conclusion.

---

## The log contract

Every demo service logs through one shared library
(`KubeSage.Workload.Shared`), so the whole workload produces one shape. That
consistency is what lets a single detection rule work across every service — if
each wired its own variant, detection would work for some and silently miss
others.

```json
{
  "ts": "2026-08-16T02:17:13.482Z",
  "level": "error",
  "service": "order-api",
  "correlationId": "9154c724613a4233",
  "msg": "Dependency payment-simulator timed out after 2001.29ms while processing ord_07c5a5bfeed3",
  "pod": "order-api-7bd5fb555b-8vktq",
  "operation": "CreateOrder",
  "dependency": "payment-simulator",
  "durationMs": 2001.29,
  "statusCode": 503
}
```

### The fields that matter

| Field | Why it exists |
| --- | --- |
| `dependency` | **The most valuable field.** Distinguishes "order-api is unhealthy" from "order-api cannot reach payment-simulator" |
| `durationMs` | Turns "it failed" into "it failed after exactly 2001ms", which is what reveals a timeout |
| `operation` | A logical name (`CreateOrder`), never a URL with identifiers in it |
| `correlationId` | Follows one request across gateway → order-api → payment-simulator |
| `pod` | From the downward API, so "this one replica is broken" is visible |

### Why a custom formatter

`WorkloadLogFormatter` exists rather than using `AddJsonConsole` because the
built-in one nests message-template arguments inside a `State` object and keeps
the original template. That is fine for a human, but detection rules parse these
lines with LogQL, and a nested, variable shape makes those queries fragile.

The flat shape here is a **contract** between the workload and the detection
layer. Renaming a field means changing detection rules.

### Correlation without tracing

Full distributed tracing is deliberately out of scope. A single identifier
passed in an `X-Correlation-Id` header and written into every log line does the
same job for far less machinery.

`CorrelationContext` uses `AsyncLocal`, so the value follows an async call chain
without every method taking it as a parameter. A `DelegatingHandler` adds it to
outgoing calls. Incoming values are sanitised — bounded to 64 characters and
stripped of anything that is not a plain identifier character — because they
arrive over HTTP and end up in log lines that later reach a model.

### Framework noise is suppressed

At `Information`, ASP.NET Core writes several lines per request — *"Executed
endpoint"*, *"Writing value of type `<>f__AnonymousType6` as Json"*. Left
enabled they would be stored in Loki, counted by the repeated-signature rule,
and eventually shown to a model as evidence, crowding out the lines that
describe what actually happened.

Framework categories are raised to `Warning`. Logging scopes are disabled too:
ASP.NET adds trace, span, connection and request identifiers as scopes, which
roughly **doubles the size of every line** with values this project has no use
for.

---

## Loki labels

Exactly four, and this is the single most consequential choice in the telemetry
layer.

| Label | Distinct values | Why it earns a label |
| --- | --- | --- |
| `job` | 1 | Static, identifies the shipper |
| `namespace` | 2 | Small, always present |
| `container` | ~10 | Equals the service name for demo workloads |
| `level` | 6 | Filtering to errors fast is the most common need |

Loki builds an index entry for **every distinct combination of label values**. A
correlation identifier as a label would create an unbounded index and make
queries progressively slower until the system became unusable.

### Everything else stays in the line

`correlationId`, `pod`, `orderId`, `durationMs`, `dependency`, `operation` — all
fully searchable through LogQL's `json` parser, at no index cost:

```logql
{namespace="kubesage-demo", container="order-api", level="error"}
  | json
  | dependency = "payment-simulator"
  | durationMs > 1000
```

### Verified in both directions

`python kubesage.py verify` asserts the four intended labels are **present** and
that a list of high-cardinality names is **absent**:

```python
forbidden = {"correlationid", "orderid", "pod", "pod_name", "requestid"}
```

Checking only for presence would let a regression add `correlationId` unnoticed.

---

## The Fluent Bit pipeline

A DaemonSet on every node, including the control plane — without that toleration
Loki's and Prometheus's own logs would never be collected.

```
tail (CRI parser)  →  kubernetes filter  →  parser (JSON)
                   →  nest (lift)        →  modify (rename)  →  loki output
```

| Stage | Job |
| --- | --- |
| `tail` | Reads `/var/log/containers/*.log` with the `cri` multiline parser — containerd writes the CRI line format, not raw JSON |
| `kubernetes` | Attaches pod metadata. Annotations and labels are **off**: large, repeated on every line, and nothing reads them |
| `parser` | Parses the application's JSON, promoting `level`, `service`, `operation`, `durationMs` to real fields |
| `nest` | Lifts the nested `kubernetes` object to top level with a `k8s_` prefix |
| `modify` | Renames to short names, drops metadata nothing reads |
| `loki` | Ships with exactly three dynamic labels plus a static `job` |

### Why the flatten-and-rename step exists

The Loki output plugin names a label after the **full record path** it came
from. Using a nested accessor directly produces a label called
`kubernetes_container_name`, which is what happened on the first attempt.
Flattening first and renaming gives the short names the platform's LogQL queries
are written against.

### Loki's automatic labels are disabled

```yaml
discover_service_name: []
discover_log_levels: false
```

Loki 3.x otherwise invents `service_name` and `detected_level` by guessing from
the record. Those duplicate labels the pipeline already sets deliberately, and
Loki's guess can *disagree* with the application's own `level`. An extra label
nobody asked for makes stored streams harder to reason about.

**A `detected_level` you will still see.** Loki attaches it to query *responses*
as structured metadata even with discovery off, so it shows up in the `stream`
map returned by `query_range`. It is not indexed — confirm with:

```bash
curl -s 'http://127.0.0.1:3100/loki/api/v1/labels'
# {"status":"success","data":["container","job","level","namespace"]}
```

The four indexed labels are what determine index size, and they are what the
verification check asserts on.

### Position tracking

`tail` keeps a database at `/var/log/flb-storage/tail.db`, so a Fluent Bit
restart neither re-sends lines (creating duplicate evidence) nor skips them.

---

## Metrics

Published by every service through the shared library.

| Metric | Labels | Read by |
| --- | --- | --- |
| `kubesage_http_requests_total` | service, operation, status_class | error-rate rule |
| `kubesage_http_request_duration_seconds` | service, operation | latency rule |
| `kubesage_dependency_duration_seconds` | service, dependency, outcome | dependency-latency discrimination |
| `kubesage_dependency_failures_total` | service, dependency, kind | dependency-failure rule |
| `kubesage_notifications_processed_total` | service, outcome | worker progress |
| `kubesage_notifications_pending` | service | queue depth |

### Label cardinality here too

`status_class` groups 2xx/4xx/5xx rather than recording every status code, which
keeps the series count small and the error-rate query simple. `operation` is a
short logical name, never a URL containing identifiers.

Measured: about 27 series per gateway pod.

### The most valuable series

`kubesage_dependency_duration_seconds` — comparing one service's dependencies
side by side is what separates a slow dependency from a broken service:

```
order-api → payment-simulator   4.821s   ← the problem
order-api → workload-database   0.009s   ← fine
```

No other single view makes that distinction so directly.

`kubesage_dependency_failures_total` splits by `kind` — `timeout`, `connection`,
`http_error`. A timeout means something is listening but slow; a connection
failure means nothing is listening. Different causes, different fixes.

### Worker metrics exist for a specific blind spot

A background worker that is running but processing nothing looks perfectly
healthy to Kubernetes. `kubesage_notifications_processed_total` and
`kubesage_notifications_pending` are the only signals that show the difference.

### Discovery

Annotation-driven, so adding a service needs no Prometheus configuration change:

```yaml
annotations:
  prometheus.io/scrape: "true"
  prometheus.io/port: "8080"
  prometheus.io/path: "/metrics"
```

Relabelling promotes `namespace`, `pod`, `workload` (from the
`app.kubernetes.io/name` label) and `node` onto every sample.

cAdvisor metrics are filtered down to four series — memory working set, CPU,
memory limit, last seen — because cAdvisor otherwise exposes far more than fits
in a small Prometheus.

---

## Kubernetes state

Answers the questions logs and metrics cannot: *was the container killed, and
why?*

`KubernetesEvidenceClient` collects pod phase, readiness, restart counts,
waiting reasons, termination reasons, exit codes, memory limits, deployment
replica counts and events.

### Both reasons, always

A pod carries two different reasons, answering different questions:

- `state.waiting.reason` — why it is waiting **now** (`CrashLoopBackOff`)
- `lastState.terminated.reason` — why it last **died** (`OOMKilled`)

Reading only the first discards the OOM signal entirely, because an OOM-killed
container immediately enters `CrashLoopBackOff`. That destroys precisely the
distinction that decides whether the fix is a memory limit or a code change.

### Restart recency

Evidence includes `minutesSinceLastRestart` and `restartIsRecent`, and the
summary says so in words:

```
pod payment-simulator-7c9 phase=Running ready=False restarts=3,
lastTermination=OOMKilled, exitCode=137, last restart 2m ago (RECENT)
```

A cumulative restart count cannot distinguish "crashing right now" from
"restarted during a deployment an hour ago". Without this, a cluster health
report read old restarts as a current problem and declared a healthy cluster
degraded.

Memory limits are included alongside pod state because `OOMKilled` only means
something next to the limit that was exceeded.

---

## Evidence

Everything observed becomes an `Evidence` record with a deterministic
content-derived identifier, a redacted one-line summary, structured attributes,
and **the exact query that produced it**.

Seven kinds, ordered by information per item — also the order they are trimmed
in when the evidence budget is exceeded:

1. `KubernetesState` — authoritative, few
2. `KubernetesEvent` — BackOff, Unhealthy, Killing
3. `Metric` — quantifies impact in one line
4. `LogSignature` — a repeated pattern with a count
5. `HistoricalIncident` / `Runbook` — context, not observation
6. `LogSample` — most numerous, least informative each

### Log signatures

During an incident the same failure is logged thousands of times, identical
except for an identifier and a duration:

```
Dependency payment-simulator timed out after 2001.29ms while processing ord_07c5
Dependency payment-simulator timed out after 1998.44ms while processing ord_09a8
                              ↓  normalise  ↓
Dependency payment-simulator timed out after <duration> while processing <id>
```

Both collapse to one signature reported once with a count. **63× the same
timeout** is better evidence than 63 near-identical lines, and costs a fraction
of the model's context.

Normalisation replaces GUIDs, timestamps, IP addresses, durations, quoted
strings, prefixed identifiers, long hex strings and numbers, then collapses
whitespace and truncates at 300 characters — a stack trace differing only in its
deepest frames would otherwise produce a new "unique" signature every time.

Signatures also carry `distinctPods`, which is diagnostic in itself: a failure
on every pod suggests a shared dependency, one on a single pod suggests
something local.

---

## Grafana

Grafana points at exactly the same Loki and Prometheus the platform queries.
That is the point: a person can open a dashboard and re-check any evidence an
agent cited, using the same source data, without trusting the platform.

The provisioned dashboard emphasises panels that **discriminate** between
failure modes:

| Panel | Discriminates |
| --- | --- |
| Server error rate by service | Whether users are affected, and where |
| Dependency latency p95, side by side | A slow dependency from a broken service |
| Dependency failures by kind | A timeout (something slow) from a connection failure (nothing listening) |
| Container memory against its limit | An OOM kill from an ordinary crash |
| Errors and warnings across the workload | The raw lines behind any cited evidence id |

Datasources are provisioned from `deploy/compose/grafana/provisioning/`, reading
their URLs from environment variables so ports stay defined in one place.

---

## Verifying without any AI

The `/evidence` endpoints expose the deterministic layer directly:

```bash
curl "http://127.0.0.1:8081/evidence?workload=order-api&windowMinutes=15"
curl "http://127.0.0.1:8081/evidence/kubernetes?workload=order-api"
curl "http://127.0.0.1:8081/evidence/log-signatures?workload=order-api"
```

During the payment-latency scenario the correlated bundle alone contains:

```
KubernetesState  order-api pods Running, ready, 0 restarts     ← rules out a pod fault
Metric           16.0% of requests returned 5xx over 5 minutes
Metric           calls to payment-simulator take 4.257s at p95 ← the culprit
Metric           calls to workload-database take 0.009s at p95 ← rules out the database
LogSignature     22x Dependency payment-simulator timed out
```

**That is the diagnosis, produced with no model involved.** The AI layer adds
explanation, correlation and historical context on top of something already
sound.

If a bundle is incomplete, the response says which sources were unreachable
rather than silently returning less:

```json
{ "isComplete": false, "unavailableSources": ["log signatures (HttpRequestException)"] }
```

An empty result that looks like "all clear" would be far more dangerous than an
error.
