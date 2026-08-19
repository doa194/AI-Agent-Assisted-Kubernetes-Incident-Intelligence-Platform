# Design decisions

The choices that shaped the system: what was chosen, what was rejected, and what
each cost.

Several were forced by **measurement** rather than preference, and those are the
most useful ones to know about — they are marked 📐. If you change one of those,
re-measure rather than reasoning from first principles.

- [Architecture](#architecture)
- [Detection](#detection)
- [Evidence quality](#evidence-quality)
- [The AI layer](#the-ai-layer)
- [Hardware-driven decisions](#hardware-driven-decisions) 📐
- [Retrieval](#retrieval)
- [Security](#security)
- [Implementation](#implementation)

---

## Architecture

### Keep the AI platform outside the cluster it watches

**Chosen:** the platform runs in Docker Compose, reaching the cluster through
fixed host ports.

**Rejected:** deploying it into the cluster as another workload. Simpler
networking, no cross-plane plumbing, one fewer moving part.

**Why:** an incident severe enough to disrupt the cluster would take down the
system meant to explain it, exactly when it is needed most.

**Cost:** real networking complexity — published ports, an extra certificate
SAN so TLS verification can stay on, and a generated kubeconfig. That complexity
is the price of the guarantee, and it is paid once at bootstrap.

---

### Deterministic detection, agentic explanation

**Chosen:** thresholds and windows decide that something is wrong; a model
decides why.

**Rejected:** letting a model read telemetry and decide what constitutes an
incident.

**Why:** detection needs to be repeatable, explainable and cheap. A rule that
fires produces a number an operator can check. A model deciding what counts as
an incident produces an opinion that varies between runs.

**Verified:** with Ollama stopped, six incidents were still detected, persisted
and queued. That is not a fallback mode — it falls out of the split.

---

### PostgreSQL as the queue instead of a broker

**Chosen:** `SELECT ... FOR UPDATE SKIP LOCKED` with a lease column.

**Rejected:** RabbitMQ or Kafka.

**Why:** the required properties are durability, idempotency, bounded retries
and concurrency limiting. PostgreSQL provides all four in a datastore that is
already mandatory for storing incidents. A broker would add a second system to
run and monitor, in exchange for throughput that is irrelevant when the model
serves about one investigation at a time.

**Cost:** polling rather than push, so work starts up to `DispatcherPollSeconds`
late. Five seconds against multi-minute investigations is noise.

**Revisit when:** many platform instances and high incident volume make polling
wasteful. See [production-considerations.md](production-considerations.md#the-work-queue).

---

### A modular monolith, not microservices

**Chosen:** one deployable application with module folders and narrow interfaces.

**Rejected:** separate services per module.

**Why:** the modules share a database, a configuration tree and a request
lifetime. Splitting them would add deployment, networking and debugging cost
without solving any problem that exists at this scale.

**Cost:** module boundaries are conventional rather than enforced by a network.
They are kept honest by each module owning its own DI registration and exposing
a small surface.

---

## Detection

### Cross-rule suppression within a pass

**Chosen:** when a more explanatory rule fires for a workload, generic
repeated-error-signature candidates for the same workload are dropped, and
multiple signatures per workload collapse to the most frequent.

**Why — measured, not theorised.** 📐 Scaling the workload database to zero
produced **twelve** candidates in one pass: the database readiness failure,
connection failures from two services, elevated error rates on two more, and
seven separate log-signature candidates for the logs those failures produced.

Every one was a true observation. **Only one was the incident.**

Twelve investigations at ~7 minutes each would occupy the model for over an hour
and bury the useful conclusion among eleven restatements of the symptom.

**Result:** twelve became **four**, each a genuinely distinct signal — user
impact on two services, the root cause, and the cascade.

**Cost:** a log-signature problem co-occurring with an unrelated metric problem
on the same workload could be masked. Accepted, because the signature rule is a
safety net for what the other rules cannot see, and it survives whenever nothing
else explains that workload.

---

### Restart *increase*, not restart count

**Chosen:** rules compare against restart counts persisted from the previous
pass.

**Why:** a pod that crash-looped last week still carries those restarts. Using
the absolute count would raise an incident every minute, forever, for a problem
that is long over.

**Cost:** a `detection_state` table and a small amount of bookkeeping. When
Kubernetes is unreachable the previous counts are deliberately **not**
overwritten — writing an empty snapshot would make every pod appear to have
restarted on the next successful pass.

---

### Only 5xx counts toward the error rate

**Chosen:** 4xx responses are excluded from the error-rate rule.

**Why:** a 4xx means the caller sent something invalid. The traffic generator
does this deliberately and continuously to provide a realistic background of
rejected requests. Counting them would make every healthy period look like an
incident, and the threshold would have to be raised until it stopped detecting
anything real.

The payment simulator also returns 402 for a declined card — normal business
behaviour, not a failure.

---

### A minimum request sample

**Chosen:** ratio rules ignore windows with fewer than 20 requests.

**Why:** one failure out of two requests is a 50% error rate. Without this, a
quiet period produces alarming ratios from nothing.

The threshold is `MinimumRequestSample`, default 20.

---

## Evidence quality

Three decisions made after watching investigations struggle with evidence that
was technically correct and practically useless. They share a theme: **the
limiting factor was not the model, it was what the model was given.**

### Recover stranded incidents at startup, not on the next detection pass

**Chosen:** a startup recovery service requeues incidents left mid-flight by a
crash, and the dispatcher resumes them from whatever state they are actually in.

**Why:** detection is fingerprint-deduplicated, so a killed process leaves an
incident in `Triaging` or `Investigating` that the *next* pass will never
re-raise — it looks like a duplicate. Without explicit recovery those incidents
would sit unfinished forever, and the deduplication that makes detection sane
would be the thing that stranded them.

**The subtlety that made this harder than it looks.** The state machine forbids
`Investigating → Triaging`, quite correctly. The dispatcher originally resumed
everything from triage, so recovery threw an invalid-transition error on the
exact path meant to rescue the work — recovery failed **permanently**, and only
for the incidents that needed it most. The dispatcher now switches on the
current state and re-enters the workflow at the right point.

**Verified:** seven unfinished incidents recovered after a kill, zero requeued
twice, no duplicate reports.

### The observed workload must explain its own readiness failures

**Chosen:** an unready service logs a throttled warning saying *why*, at most
once a minute.

**Why:** this was a genuine gap in the product, and the interesting part is
where the gap was. Kubernetes marks the pod NotReady; the probe response
explains itself in an HTTP body — and the platform reads logs, metrics and
cluster state, **never** a probe body. So an investigation into a readiness
failure saw "not ready" with no reason available anywhere.

The fix belongs in the workload, not the detector. No amount of cleverness in
the investigation layer can recover information that was never emitted.

**Throttled** because the probe runs every few seconds; logging every failure
would bury Loki in one repeated message. Once a minute is frequent enough to
land inside any detection window.

### Report both pod reasons, not the first one found

**Chosen:** `pod_summary` returns the current *waiting* reason and the *last
termination* reason separately, plus how recently the restart happened.

**Why:** a pod recovering from an OOM kill is `Waiting: CrashLoopBackOff` right
now, and `OOMKilled` is in the previous termination. Returning the first reason
found reported `CrashLoopBackOff` and discarded `OOMKilled` — leaving the agent
unable to distinguish an out-of-memory kill from an ordinary crash, which is
precisely the distinction that changes the fix.

Restart recency is included for a related reason: a restart count says nothing
about whether the restart is part of *this* incident.

---

## The AI layer

### Three agents rather than one

**Chosen:** Triage → Investigation → Report, with only Investigation holding
tools.

**Rejected:** a single agent doing all three jobs, at roughly half the total
model time.

**Why each split earns its cost:**

| Split | Buys |
| --- | --- |
| Triage separate | Declines work early. An investigation takes minutes, and a real incident waiting behind a self-resolving blip is a real cost |
| Only Investigation has tools | Narrows the blast radius. Triage and Report cannot reach the cluster at all |
| Report separate | Receives *validated findings*, not raw telemetry, so it cannot introduce a cause the investigation never reached |

**Cost:** three model calls instead of one — roughly 400 s instead of perhaps
200 s.

---

### Triage gets a deliberately small evidence slice

**Chosen:** 15 items, runbooks excluded.

**Why:** 📐 triage only decides whether to look closer. That decision does not
improve with sixty items, but the call time grows with every one. Sending the
full pool once pushed a triage call **past the model timeout**, so the cheapest
step in the pipeline became the one that killed the investigation.

---

### Validate agent output against collected evidence

**Chosen:** every claim cites evidence identifiers; unresolvable citations are
removed and unsupported claims discarded entirely.

**Rejected:** trusting schema-constrained output.

**Why:** a schema guarantees **shape**, not **truth**. A model can produce a
perfectly well-formed hypothesis citing identifiers it invented. Without
validation that would be stored and served as though it were grounded — which
would make the project's central claim false.

The validator is strict in one direction and forgiving in the other: an invented
identifier is removed and an unsupported claim rejected, but a merely *uncertain*
hypothesis is kept, because ranking uncertain possibilities is legitimate
investigation work.

---

### Evidence collected at detection time, not investigation time

**Chosen:** the evidence bundle is captured when the incident is raised.

**Why:** on this hardware an investigation may start many minutes after
detection, by which point the interesting log lines have aged out of the query
window. Copying a bounded slice into PostgreSQL is also what lets a report read
next week still show what it was based on.

**Cost:** the one place raw telemetry is duplicated out of Loki.

---

### Deterministic evidence identifiers

**Chosen:** the identifier is a hash of the content that defines the observation.

**Why:** collecting the same observation twice yields the same id, so an agent
cannot inflate apparent corroboration by asking for the same thing repeatedly.
It also means an identifier quoted in a stored report still resolves later.

Metric identifiers include a minute-rounded bucket for the same reason —
otherwise collecting a metric twice seconds apart would mint two ids for one
fact.

---

### `Inconclusive` is a first-class outcome

**Chosen:** an investigation that cannot support a conclusion says so, and that
is terminal rather than retryable.

**Why:** a confident wrong answer is worse than an honest "not enough evidence".
Making it terminal matters too — retrying an unanswerable incident forever would
consume the model indefinitely.

---

## Hardware-driven decisions

All of these were measured on a GTX 1060 6GB with 13.6 GB of Docker memory.
**Re-measure before changing them on different hardware.**

### One model resident at a time 📐

**Chosen:** `OLLAMA_MAX_LOADED_MODELS=1`, accepting a ~34 s reload per
investigation.

**Rejected:** keeping the chat and embedding models both loaded, avoiding
reloads entirely.

**Why not — this is counter-intuitive.** Keeping the embedding model resident
alongside the chat model on a 6 GB card pushed chat layers off the GPU. The
sizes that matter are the resident ones `ollama ps` reports — 681 MB and 8.1 GB
— rather than the smaller on-disk figures. Prompt processing collapsed to **14 tokens/sec** — slow enough that a
triage call exceeded a 600-second timeout and the investigation failed outright.

Reloading costs ~34 s once per investigation. Losing GPU residency costs minutes
on *every* call.

**The trade-off it introduces, found later:** with one model slot and
serialised requests, an embed request issued while a generation is running waits
for that generation **and** a model swap — ~58 s to load the embedding model
cold, on top of a generation that can take 170 s.

A 120-second embedding timeout was comfortably enough before this change and
became a spurious failure after it, reporting a healthy system as broken.
Embedding timeouts are therefore 300 s.

Inside a normal investigation this never bites — retrieval runs before triage
and the investigation owns the model slot throughout. It only appears when
something external embeds concurrently.

---

### Right-size the context window 📐

**Chosen:** 8192 tokens, with evidence trimmed to fit.

**Why:** oversizing is not free. The key/value cache is allocated up front and
competes with model weights for video memory, so a 16K window measurably pushed
layers onto the CPU.

Combined with the model-residency change, triage went from **600 s (timeout) to
106 s**.

**The rule:** trim evidence to fit the window rather than growing the window to
fit the evidence.

---

### Bound the evidence given to a model 📐

**Chosen:** `MaxEvidenceItems` enforced, with a priority order, and
`evidenceIds` capped in the schema.

**Why:** an investigation with 113 evidence items produced a hypothesis trying
to cite **44 of them**, ran past the output token limit, and returned JSON
truncated mid-array. Nine minutes of model time wasted on an unparsable answer.

Two fixes, both needed. `MaxEvidenceItems` was *configured but never enforced* —
a straightforward bug. And the schema now caps citations, because a hypothesis
supported by forty pieces of evidence is **less** discriminating than one
supported by four, not more.

The priority order — cluster state, events, metrics, signatures, past incidents,
runbooks, log samples — reflects information per item, which is roughly the
inverse of how many of each kind exist. Log samples come last precisely because
there are hundreds of them and any one line says the least.

---

### Investigation concurrency of one

**Chosen:** `MaxConcurrent = 1`.

**Why:** a local 12B model does not gain throughput from parallel
investigations. It loses to memory pressure and timeouts.

This is a hardware fit, not an architectural limit. The work queue supports
higher concurrency without change.

---

## Retrieval

### Embed summaries, never raw telemetry

**Chosen:** incident summaries, root causes and runbook sections go into
pgvector. Log lines, metric samples and Kubernetes events do not.

**Why:** embedding every log line would be expensive, grow without bound, and
make retrieval *worse* — thousands of near-identical lines would crowd out the
one incident summary that answers the question.

---

### Only conclusive investigations become memory

**Why:** recording "we could not work this out" would fill the corpus with
entries that crowd out useful history and could steer a later investigation
toward giving up.

---

### Metadata filters are a preference, not a requirement 📐

**Chosen:** filter semantic search by workload and category, but retry without
the facets when the filtered search returns nothing.

**Why:** strict filtering caused a **silent** failure. Runbooks are categorised
by the problem they describe (`dependency_latency`), but an incident can be
categorised differently (`http_error_rate`) and still be about that exact
problem. The filter excluded every runbook and retrieval returned nothing —
while looking identical to "nothing relevant existed".

The distance cut-off still does the real gating.

---

### Weak matches are excluded rather than padding top-K

**Why:** returning a weak match is worse than returning nothing. An agent handed
an unrelated past incident will try to make it fit.

---

### HNSW rather than IVFFlat

**Why:** IVFFlat needs training data to build its lists and behaves poorly on a
small, growing corpus — exactly what this is on a fresh install. HNSW works well
from the first row.

---

### Retrieval confidence is kept separate from root-cause confidence

**Why:** a strongly matching past incident can still be the wrong explanation.
Conflating text similarity with diagnostic confidence is how a system starts
confidently reporting last month's root cause for this month's outage.

---

## Security

### Structural defence against prompt injection, not filtering

**Chosen:** instructions in the system message, evidence only in the user
message inside fenced `<evidence>` blocks, each agent told plainly that evidence
text is untrusted data.

**Rejected:** detecting and stripping instruction-like text from logs.

**Why not:** it destroys real evidence, it can be worded around trivially, and a
log line reading "ignore previous instructions" is a *fact about what a service
logged* — which may itself be worth reporting.

Making the boundary explicit is both safer and honest about what the model is
reading.

---

### Read-only enforced by RBAC, not by code

**Chosen:** the service account has no write verb on anything, no access to
Secrets, and no `pods/exec` or `pods/portforward`.

**Why:** code can be changed by mistake; the API server cannot be talked into
it. Three independent layers have to fail before the cluster could be modified.

Verified by asking the API server itself with `kubectl auth can-i` rather than
by reading YAML.

---

### Label matchers are validated, not escaped

**Chosen:** a workload name that is not a valid Kubernetes name is **refused**;
free-text search terms are escaped.

**Why:** a label matcher is not a string literal. Escaping would not make a
hostile value safe there. Refusing also makes the attempt visible in logs, where
silently sanitising would hide it.

---

### Low-cardinality Loki labels

**Chosen:** exactly four labels. Correlation identifiers, pod names and order
identifiers stay inside the log line.

**Why:** Loki indexes every distinct combination of label values. A correlation
identifier as a label would create an unbounded index. Those fields remain fully
searchable through LogQL's JSON parser at no index cost.

Verified by a check that asserts the **absence** of high-cardinality labels, not
just the presence of the intended ones.

---

## Implementation

### Hand-written SQL over an ORM

**Chosen:** Npgsql and Dapper with explicit SQL and hand-written migrations.

**Why:** the two hardest pieces — the `SKIP LOCKED` work queue and pgvector
similarity search with metadata filters — are exactly where an ORM adds friction
rather than removing it. Hand-written migrations also make the schema story
completely transparent: every statement that will run against the database is
readable without learning a tool.

**Cost:** more mapping code, and one sharp edge. Dapper matches constructors by
reader column types, and Npgsql returns `DateTime` for `timestamptz`. Positional
records fail to materialise with an error naming every column and explaining
none of them. All row types are therefore plain classes with a parameterless
constructor.

---

### A custom log formatter for the workload

**Chosen:** `WorkloadLogFormatter` producing a flat, fixed JSON shape.

**Rejected:** the built-in `AddJsonConsole`.

**Why:** the built-in one nests message-template arguments inside a `State`
object and keeps the original template. That is fine for a human, but detection
rules parse these lines with LogQL, and a nested, variable shape makes those
queries fragile.

The flat shape is a **contract** between the workload and the detection layer.

---

### A hand-written options validator

**Chosen:** `KubeSageOptionsValidator` implementing `IValidateOptions<T>`
manually.

**Rejected:** `ValidateDataAnnotations`.

**Why:** that helper only checks the top-level object and would silently ignore
every attribute on the nested sections — which is where all the real settings
live. It also cannot express relationships *between* settings, which are the
mistakes that actually hurt.

Caught a real defect immediately: the shipped defaults were self-contradictory.

---

### A custom `IChatClient` for Ollama

**Chosen:** `OllamaChatClientAdapter`, written by hand.

**Rejected:** `Microsoft.Extensions.AI.Ollama` (deprecated, preview only).

**Why:** beyond availability, the adapter encodes two behaviours a generic
client gets wrong — Gemma 4 hides its output in a `thinking` channel that
`/api/generate` never surfaces, and the `think` flag must be controllable per
request because reasoning tokens are as slow as everything else.

---

### Python automation with the standard library only

**Chosen:** no third-party dependencies at all.

**Why:** a fresh clone works with nothing but a Python install. No virtual
environment, no `pip install` before the first command. For a project whose
whole point is reproducible local setup, adding a dependency-installation step
before bootstrap would undercut it.

**Cost:** a small hand-rolled `.env` parser, and `urllib` instead of `requests`.
Both are a few lines.
