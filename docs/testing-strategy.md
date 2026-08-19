# Testing strategy

What each layer protects, why the distribution looks like this, and what it has
actually caught.

- [Shape of the suite](#shape-of-the-suite)
- [The selection principle](#the-selection-principle)
- [Unit tests](#unit-tests)
- [Integration tests](#integration-tests)
- [API component tests](#api-component-tests)
- [End-to-end tests](#end-to-end-tests)
- [Operational verification](#operational-verification)
- [AI evaluation](#ai-evaluation)
- [What is deliberately not tested](#what-is-deliberately-not-tested)
- [Defects the suite has caught](#defects-the-suite-has-caught)

---

## Shape of the suite

| Layer | Count | Runtime | Needs |
| --- | --- | --- | --- |
| Unit | 99 | < 1 s | nothing |
| Integration | 25 | ~8 s | Docker |
| API / component | 11 | ~50 s | Docker |
| End-to-end | 2 | ~30 min | the full running system |
| Operational | 18 checks | ~4 min | the full running system |
| AI evaluation | 5 + 5 | ~15 min | the full running system |

```bash
dotnet test                                # unit + integration + API
python kubesage.py verify                  # operational + retrieval gold set
python kubesage.py scenario check all      # all five scenarios end to end (~20 min)
python kubesage.py e2e                     # the two critical workflows
```

`dotnet test` needs nothing running; the integration and API layers start their
own throwaway PostgreSQL. Everything below them needs a bootstrapped
environment. Avoid running `dotnet test` and a scenario check at the same time —
both lean on Docker, and host saturation shows up as unrelated-looking timeouts.

The distribution follows cost and what each layer can actually catch. E2E tests
are deliberately scarce: one takes half an hour because a real model is
involved, and anything they would duplicate is covered more cheaply elsewhere.

---

## The selection principle

The question asked before writing any test is not "is this code covered" but:

> **If this were wrong, would it fail loudly — or silently?**

Loud failures need fewer tests. The build breaks, an exception surfaces,
something obviously does not work.

Silent failures are where the tests go. A queue that drains while doing nothing.
A detection rule that never fires. Redaction that quietly destroys evidence.
Retrieval that returns nothing while looking like "nothing was relevant". Every
one of those looks *healthy* from the outside.

That principle explains the distribution better than any pyramid diagram would.

---

## Unit tests

Pure logic only — nothing touches a database, container, network or model.

### What is covered, and why each earns a place

**Configuration validation** (`KubeSageOptionsValidatorTests`)

Relationships *between* settings, not just field ranges. A work lease shorter
than the investigation timeout lets a running investigation be claimed twice.

This caught a real defect on its first run: the shipped C# defaults were
self-contradictory — lease 900 s against a timeout of 1800 s.

One test exists specifically to prove nested sections are validated at all,
because the built-in `ValidateDataAnnotations` only inspects the top-level
object and would silently ignore every attribute two levels down.

**Fingerprinting** (`IncidentFingerprintTests`)

Both failure directions are tested:

- too **coarse** → a genuinely different incident swallowed as a duplicate
- too **fine** → one outage raising an incident every minute

Including that workload order does not matter, that measured values are excluded,
and that the hash is stable across runs — deduplication has to survive a restart.

**State transitions** (`IncidentStateMachineTests`)

Every legal move, and specifically the illegal ones: skipping triage, reopening
a terminal state, moving out of `Recovered`. Also that `Inconclusive` is terminal
rather than retryable — otherwise the platform would grind against an
unanswerable incident forever.

**Redaction** (`SensitiveDataRedactorTests`)

Both halves matter equally. Secrets are removed, **and** a realistic incident log
line with durations, order ids, correlation ids and status codes passes through
*entirely unchanged*. Over-eager redaction would quietly destroy the evidence an
investigation depends on — a worse failure than the leak it prevents, because
nobody would notice.

**Query guards** (`TelemetryQueryTests`)

LogQL injection attempts refused, namespaces outside the allow-list refused,
windows clamped from the *start* (keeping recent data), and free text escaped.

One assertion is written carefully: the test does not check for the absence of
`" }` in escaped output, because `\" }` legitimately contains it. It counts
*unescaped* quotes instead — the invariant that actually matters.

**Candidate suppression** (`CandidateSuppressionTests`)

Built directly from a real observed run where one database outage produced twelve
true candidates.

**Work payload deserialisation** (`WorkItemPayloadTests`)

Added after a live bug, described below.

---

## Integration tests

Real disposable PostgreSQL via Testcontainers, using the same
`pgvector/pgvector:pg18` image the platform ships with.

Everything tested here depends on behaviour that has no meaningful in-memory
equivalent: `SKIP LOCKED`, partial unique indexes, advisory locks, vector
distance ordering, `timestamptz` handling.

| Area | Asserts |
| --- | --- |
| Migrations | Applied once, idempotent on re-run, an edited script is refused |
| pgvector | Cosine distance ranks correctly; a vector survives a round trip |
| Work queue | Idempotent enqueue, no double-claim, abandoned work reclaimed, retry then give up, lease renewal, claim limit |
| Restart recovery | Stranded incidents requeued, queued work not duplicated, terminal incidents not reopened, a resumed incident can still finish |
| Semantic memory | Upsert not duplicate, closest first, weak matches excluded, filters applied, no self-retrieval |

### Deterministic vectors, not model output

Semantic memory tests use hand-constructed vectors rather than the real
embedding model. They run fast, need no Ollama, and assert on **ranking logic**
rather than on how a particular model happens to embed a sentence.

Whether the real model retrieves sensibly is a different question, answered by
the gold set. Keeping the two apart means a model change cannot break a storage
test, and a storage bug cannot hide behind a good embedding.

### Per-test databases, not per-test containers

Starting a container takes seconds; creating a database takes milliseconds, and
gives the same isolation. Tests that apply migrations or take advisory locks
genuinely need it.

---

## API component tests

The real application in memory via `WebApplicationFactory`, against a throwaway
PostgreSQL, with **every** dependency pointed at a closed port.

That last part is the point rather than a limitation. It lets these tests assert
how the API behaves when its dependencies are **down**, which is behaviour the
project explicitly requires:

| Test | Asserts |
| --- | --- |
| Liveness with everything down | Still `200` — restarting would help nothing and destroy in-flight work |
| Readiness with only the database up | Still healthy — the platform can record, so it is ready |
| Evidence collection with telemetry down | `200` with `isComplete: false` and named unavailable sources — not a 500, and not a silent empty success |
| Detection with telemetry down | Zero incidents, no exception — the loop must survive |
| Invalid workload name | `400 query_rejected` |
| Namespace outside the allow-list | `400` naming the allow-list |
| Unknown incident state | `400` listing the allowed values |
| No reports yet | `404 no_reports_yet`, distinguishable from an error |

### Background loops are removed

A detection pass or investigation firing partway through a test would change the
data being asserted on, and the flakiness would be blamed on the test rather
than the timing. Those loops are covered by operational verification against the
running system instead.

### The fixture must point Kubernetes somewhere dead too

Originally it set Loki, Prometheus and Ollama to a closed port but left
Kubernetes alone — so the client fell back to the developer's **real
kubeconfig** and read live pods. The test's result then depended on whatever the
cluster happened to be doing, and it failed the moment a scenario check was
running in another terminal.

It now writes a temporary kubeconfig pointing at a dead endpoint.

---

## End-to-end tests

Two, against the live system with a real model.

**1. Clean start produces an automatic report.** Reset everything, restart the
platform, and confirm a startup report appears with nobody asking — stating an
overall cluster status and citing evidence it examined.

**2. Controlled incident to grounded report.** Run `payment-latency`, confirm
deterministic detection raises incidents, wait for the three-agent
investigation, and score the report against private ground truth.

They exist because nothing else can confirm the whole chain works together
against real infrastructure. They are scarce because each costs half an hour.

The second always resets the scenario in a `finally` block — a leftover fault
would contaminate every later run and look like a new incident.

---

## Operational verification

Eighteen checks that probe **real behaviour** rather than reading configuration.

The distinguishing feature of this layer: security boundaries are tested by
**attempting the forbidden action**.

```bash
# actually runs this, and expects it to fail
psql -U kubesage_app -c "CREATE TABLE probe(id int)"

# actually asks the API server, rather than reading YAML
kubectl auth can-i delete pods --as=system:serviceaccount:...:kubesage-observer
```

Fifteen `can-i` probes run on every verification — five that must be allowed and
ten that must be denied.

Other checks worth noting:

**Cross-network reachability** is the single most important environmental check.
If the operations plane cannot reach the cluster's published ports, every
telemetry query fails at runtime with an error that looks like a Loki problem
rather than a networking one.

**Label cardinality** is asserted in both directions — the four intended labels
present, and a list of high-cardinality names absent. Checking only presence
would let a regression add `correlationId` unnoticed.

**The evidence API** check exercises Loki, Prometheus and Kubernetes together
through the platform with no model involved, proving the observability half
stands on its own.

---

## AI evaluation

Model output is **never** asserted on wording. It is scored on structured
outcomes.

### Retrieval

A gold set of five realistic incident descriptions, each naming the runbook that
should be retrieved **and** a confusable one that must not outrank it.

The confusable pairs matter more than the hit rate — they are the cases where a
plausible wrong answer sends an investigation in the wrong direction.

Current result: **5/5 at rank 1**, no confusable document ranking first.

### Investigation

A finished report is scored against ground truth the agents never see:

| Check | Catches |
| --- | --- |
| Names the true root cause workload | The central claim |
| Root cause category is the right kind of problem | Wrong diagnosis with right-sounding words |
| Does not blame a workload that was only a victim | A report that stopped at the symptom |
| Every citation resolves to stored evidence | Fabricated grounding |
| Does not claim a pod crash when nothing restarted | Misread evidence |

Categories are compared by **shared significant words**. Models phrase them
differently between runs, and asserting on exact strings would test the wording
rather than the diagnosis.

Measured result on `payment-latency`: **5/5**. The incident was raised against
order-api; the report correctly identified payment-simulator, inferred the
2-second timeout from the evidence, and all six citations resolved.

---

## What is deliberately not tested

| Not tested | Why |
| --- | --- |
| Exact model wording | Varies between runs and says nothing about quality |
| Every scenario as an E2E test | All five are covered by operational checks; reproducing that at E2E level adds half an hour per scenario for no new information |
| Configuration files as unit tests | A YAML manifest is verified by applying it to a real cluster, not by asserting on parsed contents |
| Coverage targets | The suite is shaped by what fails silently, not by a percentage |
| Getters, constructors, trivial mapping | Failure here is loud and immediate |

---

## Defects the suite has caught

Recorded because each demonstrates what a given layer is *for*.

| Defect | Found by | Why it mattered |
| --- | --- | --- |
| Shipped defaults self-contradictory (lease < timeout) | Unit | Would have allowed duplicate reports |
| Work payload never deserialised (case sensitivity) | Live run → unit | **Queue drained doing nothing, looking perfectly healthy** |
| Unvalidated workload name reaching LogQL | API component | Query injection; unit tests structurally could not catch it |
| `Investigating → Triaging` forbidden on resume | Live restart → integration | Recovery failed *permanently* on the path meant to rescue it |
| Agent timeout swallowed as cancellation | Live run | Incidents stuck in `Investigating` forever |
| Over-strict metadata filter | Live run → integration | Retrieval silently returned nothing |
| Truncated JSON from unbounded evidence citations | Live run | Nine minutes of model time wasted per occurrence |
| Readiness failure produced no log evidence | Scenario check | An investigation saw "not ready" with no explanation anywhere |
| `pod_summary` discarded `OOMKilled` | Scenario check | Could not distinguish an OOM kill from an ordinary crash |
| API tests silently used the real cluster | Live run | Test results depended on unrelated cluster activity |
| Namespace allow-list could be widened but never narrowed | Live API response → unit | A security boundary that silently ignored being tightened |

### Two patterns worth drawing out

**The most dangerous defects looked healthy.** The payload bug marked work
`Completed`, logged no errors, and drained the queue normally — every observable
signal said the system was fine. The retrieval filter returned zero results,
indistinguishable from "nothing was relevant". Neither would have been found by
watching for failures.

**Several were only findable at a specific layer.** `RequireWorkload` existed and
was thoroughly unit-tested; it just was not *called*. Only a test exercising the
real HTTP path could see that. Equally, the resume bug needed a real crash and
restart — no amount of unit testing the state machine would have revealed that
the dispatcher took a forbidden route through it.

**And one that no layer would have found.** The allow-list defect lived in the
configuration binder, which every test bypassed by constructing options objects
directly. It surfaced only from reading an error message the running system
produced. Its regression test therefore binds real configuration rather than
building an options tree by hand — testing the wiring, which is where the defect
was, instead of the code around it.
