# Production considerations

Everything here describes what would need to **change**. None of it is
implemented.

KubeSage is a local development platform. Several decisions that are correct
here would be wrong in production, and it is worth separating those from the
ones that would transfer unchanged.

- [What is already production-shaped](#what-is-already-production-shaped)
- [Security](#security)
- [Scale](#scale)
- [The model](#the-model)
- [Reliability](#reliability)
- [Operational maturity](#operational-maturity)
- [Deliberate exclusions](#deliberate-exclusions)
- [Honest assessment](#honest-assessment)

---

## What is already production-shaped

Worth stating first, because it is a shorter list than the gaps but the more
important one. These were not simplified:

**The deterministic/agentic split.** Detection works without the model, and
agent output is validated against collected evidence. This is the right
architecture at any scale — arguably more important at scale, where nobody has
time to check every report by hand.

**Read-only enforcement via RBAC.** The service account has no write verb.
Three independent layers protect the cluster, and the boundary is verified by
asking the API server rather than by reading YAML. The permission model
transfers unchanged.

**Durable, idempotent work processing.** Restart recovery, lease renewal and
bounded retries behave correctly under a real kill test — seven in-flight
incidents recovered with zero duplicates.

**Low-cardinality telemetry labels.** This matters *far more* at scale, not
less. An unbounded Loki index is survivable on a laptop and fatal in production.

**Secret redaction before evidence reaches a model**, with the over-redaction
risk tested explicitly.

**No private model reasoning persisted.** There is no chain-of-thought column
anywhere in the schema.

**Evidence carrying its own query.** Reproducibility is not a debugging
convenience; it is what makes a report auditable.

---

## Security

| Current | Production would need |
| --- | --- |
| Passwords in plain text in `docker-compose.yml` | A secret manager, or at minimum Kubernetes Secrets with encryption at rest |
| Grafana anonymous viewer access | Real authentication, ideally SSO |
| KubeSage API unauthenticated | Authentication **and** authorisation — incident data is sensitive operational intelligence |
| Plain HTTP between components | TLS everywhere, with certificate rotation |
| Long-lived service account token | Short-lived projected tokens, or workload identity |
| Single shared `kubesage_app` role | Separate roles per concern if the platform grows |
| `insecure_skip_verify` for the kubelet scrape | Proper certificate plumbing |

The RBAC model itself is production-ready. It is the **credential delivery** that
is simplified — a long-lived token in a mounted file rather than a projected,
rotating one.

One point deserves emphasis: **the API is currently unauthenticated and exposes
incident reports, evidence, and cluster state.** That is a meaningful amount of
information about a system's weaknesses. It is fine on `127.0.0.1` on a
developer machine and unacceptable anywhere else.

---

## Scale

The current design assumes one cluster, one workload namespace, a few dozen
pods, and one investigation at a time.

### The work queue

PostgreSQL with `SKIP LOCKED` is right for one instance polling every five
seconds. With many platform instances and high incident volume, polling becomes
wasteful and a broker starts to earn its complexity.

**This is precisely the point at which the decision recorded in
[design-decisions.md](design-decisions.md) should be revisited.** It was the
right call for the constraints described there; those constraints would no
longer hold.

### Detection

Rules query Prometheus per service, sequentially. With hundreds of services that
becomes slow. Production would want Prometheus recording rules, or a single
aggregate query per rule rather than a loop.

The rules themselves are pure functions of a snapshot, so this is a change to
`BuildSnapshotAsync` rather than to the rules.

### Evidence storage

Evidence is copied into PostgreSQL per incident. At high incident volume that
grows without bound and needs a retention policy — probably tiered, keeping
evidence for recent and unresolved incidents longer than for old resolved ones.

### Semantic memory

HNSW handles a small corpus well. With hundreds of thousands of incidents:

- index parameters (`m`, `ef_construction`) need tuning
- re-indexing becomes a scheduled operation rather than a startup task
- the corpus needs curation — old incidents describing since-fixed problems will
  start to mislead

That last point is underappreciated. A memory that only grows eventually
retrieves obsolete explanations with high confidence.

### Loki and Prometheus

Both run as single instances with local storage and short retention (7 days and
6 hours). Production needs object storage, real retention, and replication.

---

## The model

This is the largest gap between local and production.

| Current | Production would need |
| --- | --- |
| Gemma 4 12B on one small GPU, ~6 tokens/sec | A larger model, or a served endpoint with real throughput |
| One investigation at a time | Horizontal scaling of inference |
| ~7 minutes per investigation | Minutes matter during an incident |
| One model for all four agents | Possibly a small fast model for triage, a larger one for investigation |
| No output caching | A recurring incident could reuse prior analysis |

`MaxConcurrent = 1` exists because of this hardware. On real inference capacity
it should rise, and the work queue already supports that without change.

### Model behaviour needs monitoring

There is currently **no tracking** of:

- how often reports come back inconclusive
- how often validation rejects hypotheses
- whether accuracy drifts after a model upgrade
- how often a report is later judged wrong

The data exists — agent executions, validation problems, outcomes and durations
are all persisted — but nothing watches trends. Without that, a model upgrade
that quietly degrades diagnosis quality would go unnoticed until someone
happened to distrust a report.

This is probably the single highest-value addition for a real deployment.

---

## Reliability

**Single points of failure.** One platform instance, one PostgreSQL, one Ollama.
The platform *can* run multiple instances safely — the queue is designed for it
— but this has never been tested with more than one.

**Nothing watches the platform.** It detects incidents in the observed cluster,
but a stuck dispatcher, a full disk, or a silently failing indexer would go
unnoticed. It should export its own metrics and be monitored like any other
service. The irony is not lost.

**No backup.** The incident database holds the entire operational history and
semantic memory. Losing it loses everything the system has learned.

**Recovery is tested, but only for a clean kill.** Partial network failures,
disk-full conditions, PostgreSQL failover and split-brain between instances are
not exercised.

**Host saturation is a real failure mode.** Observed during testing: running
multiple verification harnesses concurrently made the Kubernetes API and then
the Docker daemon unresponsive. In production the platform would be on separate
capacity from the cluster it watches — but this is a reminder that an observer
consuming significant resources can affect what it observes.

---

## Operational maturity

Missing, and each expected in a real deployment:

**Report feedback.** No way for an operator to mark a report correct or wrong.
This is the most valuable signal for improving the system *and* for deciding
whether to trust it. Its absence is the biggest gap in the operational story.

**Incident linking.** Related incidents from one outage are suppressed but not
explicitly linked, so the causal relationship between them is not visible.

**Runbook management.** The corpus is compiled into the assembly. Production
would want it editable without a rebuild — a mounted volume or a database table
with an admin path.

**Audit trail for reads.** Every Kubernetes read is legitimate, but there is no
durable record of which agent asked for what beyond the tool-use log on an
investigation.

**Configuration reload.** Changes require a restart.

**Alert routing.** Reports appear in the API and the log stream. Nothing pages
anyone, posts to a channel, or opens a ticket.

---

## Deliberate exclusions

| Excluded | Reconsider when |
| --- | --- |
| Distributed tracing | Investigations regularly cannot answer "which request path" — correlation IDs stop being enough |
| Autonomous remediation | Report accuracy is measured over a long period and trusted. **Human approval should remain** |
| ML anomaly detection | Static thresholds demonstrably miss real incidents. They mostly do not |
| Multi-cluster | More than one cluster needs watching. The read-only identity model extends naturally |
| A custom frontend | Grafana and the API stop being enough |
| A message broker | Instance count and incident volume genuinely exceed what polling handles |

### On autonomous remediation

This deserves more than a table row.

**The read-only boundary is the single most important safety property of this
design.** A system that can be wrong about a root cause — and this one can, as
its own `Inconclusive` outcomes attest — must not also be able to act on that
conclusion.

The failure mode is not hypothetical. In the payment-latency scenario, the
naive-but-plausible conclusion is "order-api is broken, restart it". Acting on
that would restart a healthy service, briefly mask the symptom, and leave the
actual cause untouched — while producing evidence that the remediation "worked".

Human-approved remediation *proposals* are the furthest this should reasonably
go, and even then the approval must be informed by the evidence, not just the
conclusion.

---

## Honest assessment

**What this system demonstrates well:**

- a clean separation between deterministic observation and AI explanation, with
  the boundary enforced rather than described
- evidence grounding that rejects unsupported claims instead of softening them
- a security boundary verified by the API server rather than asserted in a
  comment
- durable autonomous processing that survives a real kill test
- honest failure modes — `Inconclusive` is a real outcome, partial evidence is
  reported as partial, and degraded dependencies are visible rather than hidden

**What it does not demonstrate:**

- operating at scale, under sustained load, or with multiple instances
- behaviour over a long enough period to know whether investigation quality
  holds up as the memory corpus grows
- accuracy across a wide range of failure modes — five controlled scenarios is a
  small sample
- what happens when the observed system is genuinely unfamiliar, rather than a
  demo workload whose failure modes were designed alongside the detector

The measured results in this documentation are real and reproducible. They are
also from one machine, five scenarios, and a handful of runs. That is enough to
show the architecture works; it is not enough to claim the diagnoses would
generalise.
