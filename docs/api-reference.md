# API reference

Base URL: `http://127.0.0.1:8081` (configurable via `PLATFORM_HOST_PORT`).

Everything is read-only except `POST /analysis/run`, which exists for
diagnostics. There is no authentication — see [security.md](security.md).

All responses are JSON with camelCase property names. Enums are serialised as
strings, and null properties are omitted rather than emitted as `null`.

Timestamps are ISO 8601 carrying an explicit `+00:00` offset rather than a `Z`
suffix — `2026-08-16T08:09:36.818044+00:00`. Fractional precision varies because
trailing zeros are trimmed, so parse the offset form rather than assuming a
fixed width.

- [Health](#health)
- [Incidents](#incidents)
- [Reports](#reports)
- [Evidence](#evidence)
- [Status and analysis](#status-and-analysis)
- [Error shapes](#error-shapes)

---

## Health

Three endpoints answering three different questions. The distinction matters
operationally.

### `GET /health/live`

**Is the process alive?** Runs no dependency checks at all, so a database outage
never causes a restart loop that would destroy in-flight work.

```json
{ "status": "Healthy", "totalDurationMs": 0, "checks": [] }
```

Always `200` when the process is running.

### `GET /health/ready`

**Can the platform do its job?** Only checks tagged `ready` — currently just the
database, because a platform that cannot record what it finds has nothing to
offer.

```json
{
  "status": "Healthy",
  "totalDurationMs": 14.6,
  "checks": [
    {
      "name": "database",
      "status": "Healthy",
      "description": "PostgreSQL reachable, pgvector 0.8.6.",
      "durationMs": 11.0
    }
  ]
}
```

`200` when healthy, `503` when not.

> **Telemetry and the model are deliberately excluded from readiness.** With
> Ollama down the platform still detects incidents, stores them, and serves
> everything already known — it simply queues investigations. Taking it out of
> rotation for that would lose more than it protects.

### `GET /health/detail`

**Everything, including what readiness hides.** Always returns `200` — it is a
status report, not a probe.

```json
{
  "status": "Degraded",
  "totalDurationMs": 11.8,
  "checks": [
    { "name": "database",  "status": "Healthy", "error": null, "durationMs": 0.7,
      "description": "PostgreSQL reachable, pgvector 0.8.6." },
    { "name": "telemetry", "status": "Healthy", "error": null, "durationMs": 10.7,
      "description": "All telemetry sources reachable." },
    { "name": "model",     "status": "Degraded", "error": null, "durationMs": 6.3,
      "description": "Ollama is unreachable. Detection continues and investigations are queued until it returns." }
  ]
}
```

`error` carries the exception message when a check throws, as opposed to
reporting a degraded state it understands. All three health endpoints share this
shape; `/health/live` simply has an empty `checks` array.

This is where a degradation becomes visible. Surfaced by
`python kubesage.py status`.

---

## Incidents

### `GET /incidents`

| Parameter | Type | Default | Notes |
| --- | --- | --- | --- |
| `state` | string | all | One of `Candidate`, `Triaging`, `Investigating`, `Reported`, `Ignored`, `Inconclusive`, `Failed`, `Recovered` |
| `limit` | int | 50 | Clamped to 1–200 |

```bash
curl "http://127.0.0.1:8081/incidents?state=Reported&limit=10"
```

```json
[
  {
    "id": "01a008f1-b891-728d-86b9-1d2bd04ae1be",
    "state": "Reported",
    "severity": "Critical",
    "category": "http_error_rate",
    "title": "order-api is returning 72.6 % server errors",
    "affectedWorkloads": ["order-api"],
    "firstDetectedAtUtc": "2026-08-16T03:40:59.888000+00:00",
    "lastDetectedAtUtc": "2026-08-16T03:46:02.114000+00:00",
    "occurrenceCount": 6
  }
]
```

`occurrenceCount` is how many times the condition was observed. A rule that
fired once and one that has fired forty times in a row are different situations.

An unknown state returns `400` listing the allowed values.

### `GET /incidents/{id}`

The incident plus **all** its evidence — the two are only meaningful together.

```json
{
  "incident": {
    "id": "01a008f1-b891-728d-86b9-1d2bd04ae1be",
    "fingerprint": "a3f2b8c91d04e7f65b28",
    "state": "Reported",
    "severity": "Critical",
    "category": "http_error_rate",
    "title": "order-api is returning 72.6 % server errors",
    "detectionRule": "http-error-rate",
    "namespace": "kubesage-demo",
    "affectedWorkloads": ["order-api"],
    "signals": {
      "errorRatio": "0.7260",
      "threshold": "0.1000",
      "totalRequests": "88",
      "windowMinutes": "5"
    },
    "firstDetectedAtUtc": "2026-08-16T03:40:59.888000+00:00",
    "lastDetectedAtUtc": "2026-08-16T03:46:02.114000+00:00",
    "updatedAtUtc": "2026-08-16T03:47:51.204000+00:00",
    "occurrenceCount": 6,
    "outcome": "root cause identified in payment-simulator"
  },
  "evidence": [
    {
      "id": "met_4ca860bed341",
      "kind": "Metric",
      "source": "prometheus",
      "observedAtUtc": "2026-08-16T03:41:02.001000+00:00",
      "workload": "order-api",
      "summary": "order-api: calls to payment-simulator take 4.821s at the 95th percentile",
      "attributes": { "dependency": "payment-simulator", "p95Seconds": "4.8210" },
      "query": "histogram_quantile(0.95, ... kubesage_dependency_duration_seconds_bucket)"
    }
  ]
}
```

`signals` is the arithmetic that made the rule fire, so the decision can be
checked rather than trusted.

`outcome` is a short plain-language note on how the incident ended, and is
omitted while one is still open. A `Recovered` incident additionally carries
`recoveredAtUtc` and an outcome such as
`"condition not observed for 10 minutes"` — the platform noticing on its own
that the problem stopped.

Returns `404` with `{ "error": "incident_not_found", "id": "..." }` if unknown.

---

## Reports

Two kinds, distinguished by `kind`:

- `incident` — explains one failure; has `incidentId` and `investigationId`
- `startup-analysis` / `scheduled-analysis` — whole-cluster health; both are
  `null`, and `severity` carries the cluster status (`healthy`, `degraded`,
  `unhealthy`)

### `GET /reports`

| Parameter | Type | Default | Notes |
| --- | --- | --- | --- |
| `limit` | int | 20 | Clamped to 1–100 |

### `GET /reports/latest`

The most recent report of any kind. `404` with
`{ "error": "no_reports_yet" }` before any investigation has run — a normal
state on a fresh install, and distinguishable from an error.

```json
{
  "id": "01a008f1-cbe1-7444-bdc8-f44b60143160",
  "kind": "incident",
  "incidentId": "01a008f1-b891-728d-86b9-1d2bd04ae1be",
  "investigationId": "01a008f1-cbe1-7444-bdc8-f44b60143160",
  "title": "order-api is returning 72.6 % server errors",
  "summary": "The `order-api` service is experiencing a high rate of 503 errors (72.6%) due to a downstream dependency failure...",
  "severity": "Critical",
  "affectedWorkloads": ["order-api"],
  "impact": "The `order-api` service is failing to complete `CreateOrder` operations because the `payment-simulator` is consistently taking over 4 seconds to respond (met_4ca860bed341)...",
  "timeline": [
    "03:40 — error rate begins rising on order-api",
    "03:41 — payment-simulator p95 reaches 4.8s"
  ],
  "likelyRootCause": "The root cause is a performance degradation or timeout in the `payment-simulator` service, which `order-api` depends on.",
  "rootCauseCategory": "dependency_latency",
  "confidence": 0.95,
  "alternativeHypotheses": [],
  "recommendedActions": [
    "Investigate payment-simulator resource usage and its own downstream calls",
    "Review whether the 2-second timeout in order-api is appropriate"
  ],
  "verificationSteps": [
    "Compare p95 latency for order-api's two dependencies in Grafana"
  ],
  "evidenceIds": [
    "log_6cb5aeb8fffe", "log_fe140fc60059", "sig_e11d7e519f88",
    "met_4ca860bed341", "met_7379ecbb11e9", "met_2b036645bee6"
  ],
  "createdAtUtc": "2026-08-16T03:47:51.204000+00:00"
}
```

`recommendedActions` are suggestions for a human. The platform never performs
them and has no permission to.

### `GET /reports/{id}/evidence`

**The endpoint that makes a report verifiable.** Resolves every cited identifier
to the actual observation, including the query that produced it.

```json
{
  "report": { "...": "as above" },
  "citedEvidence": [
    {
      "id": "met_4ca860bed341",
      "kind": "Metric",
      "source": "prometheus",
      "observedAtUtc": "2026-08-16T03:41:02.001000+00:00",
      "workload": "order-api",
      "summary": "order-api: calls to payment-simulator take 4.821s at the 95th percentile",
      "query": "histogram_quantile(0.95, sum by (service, dependency, le) (rate(kubesage_dependency_duration_seconds_bucket[5m])))"
    },
    {
      "id": "sig_e11d7e519f88",
      "kind": "LogSignature",
      "source": "loki",
      "summary": "63x [error] Dependency payment-simulator timed out after <duration> while processing <id>",
      "query": "{namespace=\"kubesage-demo\", container=\"order-api\"} | json | level=~\"error|warn\""
    }
  ]
}
```

If `citedEvidence` is shorter than `evidenceIds`, the validator removed
fabricated identifiers before storing — which is the system working, not
failing.

For a cluster report, `citedEvidence` is empty with an explanatory `note`, since
its evidence is not attached to a single incident.

---

## Evidence

These expose the deterministic layer directly, with **no AI involvement**. If
they return good evidence, the observability half is sound on its own.

### `GET /evidence`

A correlated bundle: Kubernetes state, deployment status, events, metrics, log
signatures and log samples.

| Parameter | Type | Default | Notes |
| --- | --- | --- | --- |
| `workload` | string | all | Must be a valid lower-case Kubernetes name |
| `windowMinutes` | int | 15 | Clamped to 1–`MaxQueryRangeMinutes` (120) |

```bash
curl "http://127.0.0.1:8081/evidence?workload=order-api&windowMinutes=15"
```

```json
{
  "collectedAtUtc": "2026-08-16T03:41:10.552000+00:00",
  "windowStartUtc": "2026-08-16T03:26:10.552000+00:00",
  "windowEndUtc": "2026-08-16T03:41:10.552000+00:00",
  "namespace": "kubesage-demo",
  "workload": "order-api",
  "isComplete": true,
  "unavailableSources": [],
  "itemCount": 54,
  "items": {
    "KubernetesState": [ { "id": "k8s_b49b6d62ceac", "summary": "pod order-api-7b6 phase=Running ready=True restarts=0", "...": "" } ],
    "Metric":          [ { "id": "met_635b1f1fe6d8", "summary": "order-api: 16.0 % of requests returned 5xx over the last 5 minutes (122 requests)", "...": "" } ],
    "LogSignature":    [ { "id": "sig_e11d7e519f88", "summary": "22x [error] Dependency payment-simulator timed out after <duration>", "...": "" } ],
    "LogSample":       [ { "id": "log_1a5ba969d457", "summary": "[error] CreateOrder failed with status 503 in 2001.0163ms", "...": "" } ]
  }
}
```

**`isComplete` and `unavailableSources` matter.** A partial bundle names what
could not be reached rather than silently returning less. An empty result that
looks like "all clear" would be far more dangerous than an error.

### `GET /evidence/kubernetes`

Cluster state only — the fastest useful question during an incident, since it
needs neither Loki nor Prometheus.

| Parameter | Type | Default |
| --- | --- | --- |
| `workload` | string | all |
| `ns` | string | `kubesage-demo` |
| `sinceMinutes` | int | 30 (clamped 1–240) |

```json
{
  "pods":        [ { "id": "k8s_...", "summary": "pod payment-simulator-7c9 phase=Running ready=False restarts=3, lastTermination=OOMKilled, exitCode=137, last restart 2m ago (RECENT)" } ],
  "deployments": [ { "id": "k8s_...", "summary": "deployment payment-simulator: desired=1 ready=0 available=0 updated=1" } ],
  "events":      [ { "id": "evt_...", "summary": "[Warning] BackOff on Pod payment-simulator-7c9: Back-off restarting failed container" } ]
}
```

Pod evidence reports **both** the waiting reason and the last termination
reason, plus how long ago the restart happened. A cumulative restart count
cannot distinguish "crashing now" from "restarted during a deploy an hour ago".

### `GET /evidence/log-signatures`

Repeated error patterns with counts — usually the most informative single view.

| Parameter | Type | Default |
| --- | --- | --- |
| `workload` | string | all |
| `ns` | string | `kubesage-demo` |
| `windowMinutes` | int | 15 (clamped 1–240) |

```json
[
  {
    "id": "sig_e11d7e519f88",
    "kind": "LogSignature",
    "summary": "63x [error] Dependency payment-simulator timed out after <duration> while processing <id>",
    "attributes": {
      "occurrences": "63",
      "signatureHash": "e11d7e519f88a4c2",
      "level": "error",
      "firstSeenUtc": "2026-08-16T03:36:41.220000+00:00",
      "lastSeenUtc": "2026-08-16T03:41:08.905000+00:00",
      "distinctPods": "2"
    },
    "query": "{namespace=\"kubesage-demo\", container=\"order-api\"} | json | level=~\"error|warn\""
  }
]
```

`distinctPods` is diagnostic: a failure on every pod suggests a shared
dependency, while one on a single pod suggests something local to it.

---

## Status and analysis

### `GET /cluster/status`

Open incidents and queue depth — how an operator sees work piling up behind a
slow or absent model.

```json
{
  "openIncidents": 7,
  "incidentsByState": { "Candidate": 6, "Investigating": 1 },
  "workQueue": { "Pending": 7, "Claimed": 1, "Completed": 12, "Failed": 1 }
}
```

| Queue state | Meaning |
| --- | --- |
| `Pending` | Waiting to be claimed |
| `Claimed` | Being worked on now |
| `Completed` | Finished |
| `Failed` | Exhausted its retries — needs a look |

### `POST /analysis/run`

Forces a detection pass. Autonomous detection runs every 60 seconds anyway; this
exists so an operator can force one while testing.

```bash
curl -X POST http://127.0.0.1:8081/analysis/run
```

```json
{
  "evaluatedAtUtc": "2026-08-16T03:40:59.888000+00:00",
  "candidatesEvaluated": 7,
  "incidentsCreated": 7,
  "repeatObservations": 0
}
```

- `candidatesEvaluated` — candidates **after** suppression
- `incidentsCreated` — genuinely new incidents
- `repeatObservations` — deduplicated against existing incidents

A healthy cluster returns zeros. With telemetry unavailable it also returns
zeros rather than failing — detection cannot see anything, so it must find
nothing.

---

## Error shapes

| Status | Body | Meaning |
| --- | --- | --- |
| `400` | `{ "error": "query_rejected", "detail": "..." }` | Invalid workload or namespace outside the allow-list |
| `400` | `{ "error": "unknown_state", "allowed": [...] }` | Unrecognised incident state filter |
| `404` | `{ "error": "incident_not_found", "id": "..." }` | No such incident |
| `404` | `{ "error": "no_reports_yet" }` | Nothing generated yet — not an error condition |
| `503` | `{ "error": "telemetry_unavailable", "detail": "..." }` | Loki or Prometheus unreachable |

The distinction between `400` and `503` is deliberate: a rejected query means
the caller asked for something it may not have, while `503` means a dependency
is down. They need different responses, and callers rely on telling them apart.

### Rejected queries

```bash
curl 'http://127.0.0.1:8081/evidence/log-signatures?workload=order-api"}%20|%3D%20"secret'
```

```json
{
  "error": "query_rejected",
  "detail": "'order-api\"} |= \"secret' is not a valid workload name. Expected a lower-case Kubernetes name."
}
```

Label matchers are **validated, not escaped** — a label matcher is not a string
literal, so escaping would not make a hostile value safe there. Free-text line
filters are escaped and length-bounded instead. See [security.md](security.md).
