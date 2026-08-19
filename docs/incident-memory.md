# Semantic incident memory

The platform accumulates operational knowledge from two sources: a curated
runbook corpus shipped with it, and its own past incidents.

- [What is embedded, and what is not](#what-is-embedded-and-what-is-not)
- [Storage](#storage)
- [The runbook corpus](#the-runbook-corpus)
- [Indexing](#indexing)
- [Retrieval](#retrieval)
- [Keeping retrieval honest](#keeping-retrieval-honest)
- [Evaluating quality](#evaluating-quality)
- [The loop, demonstrated](#the-loop-demonstrated)

---

## What is embedded, and what is not

**Embedded** — short, high-value text describing a *problem* in language someone
would recognise when hitting it again:

- incident summaries, root causes, recommended actions
- normalised error signatures
- runbook sections

**Not embedded** — raw log lines, metric samples, individual Kubernetes events.

That exclusion is a design decision, not an omission. Embedding every log line
would be expensive, would grow without bound, and would make retrieval **worse**:
thousands of near-identical lines would crowd out the one incident summary that
actually answers the question.

The text embedded for an incident is deliberately written as a *problem
description* rather than a report extract:

```
Incident: order-api is returning 72.6 % server errors
Category: http_error_rate
Affected workloads: order-api
Severity: Critical

Symptoms: order-api is returning 72.6 % server errors
The order-api service is experiencing a high rate of 503 errors due to a
downstream dependency failure...

Root cause (dependency_latency): performance degradation in payment-simulator,
causing it to exceed the 2-second timeout configured in order-api.

Impact: ...
Resolution guidance: Investigate payment-simulator resource usage...
```

Symptoms first, then the cause that explained them — because a future search
starts from symptoms.

---

## Storage

One table, `semantic_memory`, holding both kinds. Relational facets sit beside
the vector so a search can be narrowed by ordinary SQL before comparing vectors.

| Column | Purpose |
| --- | --- |
| `kind` | `incident` or `runbook` |
| `incident_id` | Links to the source incident, cascading on delete |
| `source_ref` | Stable identity — an incident id, or `file#section` |
| `title` | Shown in retrieval results |
| `content` | The text that was embedded, kept for display and re-indexing |
| `content_hash` | Skips re-embedding unchanged content |
| `workload`, `category`, `root_cause_category`, `severity` | Filter facets |
| `embedding` | `vector(768)`, matching EmbeddingGemma |
| `occurred_at_utc` | When the remembered thing happened |

### Upsert, not insert

```sql
CREATE UNIQUE INDEX idx_semantic_memory_source ON semantic_memory (kind, source_ref);
```

Indexing is an upsert keyed on `(kind, source_ref)`. Without this, every restart
would add another copy of every runbook, and those copies would then **compete
with each other** for the top-K slots in every search.

### Why HNSW and not IVFFlat

```sql
CREATE INDEX idx_semantic_memory_embedding
    ON semantic_memory USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
```

IVFFlat needs training data to build its lists and behaves poorly on a small,
growing corpus — which is exactly what this is on a fresh install. HNSW works
well from the very first row.

The operator class is `vector_cosine_ops`, matching the `<=>` cosine distance
operator the queries use.

### 768 dimensions is a schema contract

Fixed at schema level on purpose. Changing embedding model requires a
**migration and a full re-index**, because vectors from two different models are
not comparable. Making that a schema change rather than a configuration change
is deliberate.

`EmbeddingClient` validates the returned dimension on every call and fails with
an explanation naming both numbers, rather than letting a mismatch surface much
later as an obscure insert error.

---

## The runbook corpus

Five runbooks in `knowledge/runbooks/`, split into 25 embedded sections, covering
the failure modes this system can actually produce:

| Runbook | Category it addresses |
| --- | --- |
| `dependency-latency.md` | `dependency_latency` |
| `pod-crash-loop.md` | `pod_restart_loop` |
| `out-of-memory.md` | `out_of_memory` |
| `database-unavailable.md` | `dependency_unavailable` |
| `readiness-failure.md` | `readiness_failure` |

Each is structured the same way:

| Section | Contains |
| --- | --- |
| **Symptoms** | What you observe |
| **How to confirm** | Which specific signals settle it |
| **Likely causes** | What tends to produce this |
| **What NOT to conclude** | The mistake this failure mode invites |
| **Recommended actions** | What a human should do |

**"What NOT to conclude" is the most valuable section.** It names the specific
error each failure mode tempts you into — for dependency latency, that the
service showing errors is the cause; for out of memory, that it was an
application crash. That is exactly the reasoning an investigation needs help
with.

### Sections, not whole documents

A whole runbook covers symptoms, causes and actions at once, and embedding all
of it produces a vague average that matches everything weakly. One section is a
single coherent idea and matches sharply.

The document title is repeated into each section's embedded text, so a section
called "Symptoms" is not embedded as generic prose detached from the problem it
describes.

Runbooks are compiled into the assembly as embedded resources, so the knowledge
an agent can retrieve is part of the build rather than something that has to be
copied onto the machine separately.

---

## Indexing

### Runbooks, at startup

`RunbookIndexingService` runs in the background shortly after start — not
blocking startup, because the platform should begin detecting immediately and
retrieval becoming available a few seconds later costs nothing. Blocking would
also mean an Ollama outage could prevent the platform starting at all, which is
exactly the coupling the rest of the design avoids.

It waits for the embedding model to become reachable (up to ten attempts), then
embeds only sections whose content hash has changed. On a normal restart this
costs nothing.

Embedding is batched — one request for twenty sections rather than twenty
requests, because per-request overhead dominates for a model this small.

### Incidents, after a report

`SemanticMemoryIndexer.IndexIncidentAsync` runs when an investigation produces a
report.

**Only conclusive investigations are indexed.** Recording "we could not work this
out" would fill the corpus with entries that crowd out useful history and could
steer a later investigation toward giving up.

Failure to index is logged as a warning, not an error — losing a memory entry is
a shame but not a failure of the investigation, whose report is already stored.

---

## Retrieval

Two paths into the same store.

### Seeded automatically, before triage

`InvestigationWorkflow.AddRetrievedContextAsync` runs during deterministic
evidence collection. This matters: an investigation that makes **no tool calls**
— common when the pre-collected evidence is already sufficient — would otherwise
never see the platform's own history at all.

The query is built to match the shape memory entries are written in:

```
"{incident title}. Category {category} affecting {workloads}."
```

### Requested explicitly by the agent

`SearchSimilarIncidents` and `SearchRunbooks` are two of the nine tools, so the
Investigation agent can go looking when the seeded context is not enough.

---

## Keeping retrieval honest

Three rules, each preventing a specific way retrieval can mislead.

### Weak matches are excluded, not padded

Results beyond `MaxDistance` (0.65 cosine) are dropped even if that means
returning fewer than `TopK`.

**Returning a weak match is worse than returning nothing.** An agent handed an
unrelated past incident will try to make it fit.

### An incident never retrieves itself

The current incident is excluded by id. Without that, a search for "incidents
like this one" returns its own summary, which reads as **strong corroboration of
whatever it already said**.

`InvestigationToolFactory` sets `CurrentIncidentId` per investigation for exactly
this reason.

### Filters are a preference, not a requirement

Filtering by workload and category before comparing vectors improves precision.
But on a small corpus it can exclude everything, so a filtered search that finds
nothing is retried without the facets.

This was found in a live run, not reasoned about in advance. Runbooks are
categorised by the problem they describe (`dependency_latency`), while an
incident can be categorised differently (`http_error_rate`) and still be about
that exact problem. The strict filter excluded every runbook and retrieval
returned **nothing** — while looking identical to "nothing relevant existed",
which is the worst possible failure mode.

Similarity and the distance cut-off still do the real gating.

### Retrieval confidence is not root-cause confidence

Retrieved memories become `Evidence` like anything else, so agents cite them the
same way and the validator checks them the same way. But they are marked as a
**different kind** and labelled explicitly in the prompt:

```
PAST INCIDENT (not evidence about the current one, retrieval confidence 78%):
  order-api errors caused by payment-simulator latency
  ...

RUNBOOK GUIDANCE (documentation, not an observation, retrieval confidence 71%):
  Downstream dependency latency - What NOT to conclude
  ...
```

A past incident is **not evidence about the current one** — it is a hint about
where to look. Blurring that is how a system starts confidently reporting last
month's root cause for this month's outage, and it is the main risk retrieval
introduces.

`retrievalConfidence` (derived purely from text distance) is stored as a separate
attribute from any confidence an agent assigns to a diagnosis. A strongly
matching past incident can still be the wrong explanation.

Evidence also carries the *original* occurrence time rather than the retrieval
time, so a report's timeline cannot accidentally place a past incident in the
present.

---

## Evaluating quality

A similarity score proves nothing on its own. What matters operationally is
whether the **right** document appears in the top-K an agent will actually see.

Retrieval is therefore scored against a gold set of five realistic incident
descriptions, each naming the runbook that should be found **and** a confusable
one that must not outrank it.

```python
RetrievalCase(
    name="payment latency",
    query="order-api is returning 503 server errors. Category dependency_latency "
          "affecting order-api. Calls to payment-simulator time out after 2 "
          "seconds. No pods restarted.",
    expected_source_prefix="dependency-latency",
    must_not_rank_first="pod-crash-loop",   # timeouts could look like a crash
)
```

```bash
python kubesage.py verify     # includes the gold-set evaluation
```

Current results against the real model and the real indexed corpus:

| Case | Expected runbook | Rank | Distance | Must not rank first |
| --- | --- | --- | --- | --- |
| payment latency | `dependency-latency` | 1 | 0.329 | `pod-crash-loop` ✅ |
| container out of memory | `out-of-memory` | 1 | 0.269 | `pod-crash-loop` ✅ |
| database unavailable | `database-unavailable` | 1 | 0.285 | `dependency-latency` ✅ |
| readiness probe failing | `readiness-failure` | 1 | 0.232 | `pod-crash-loop` ✅ |
| crash loop | `pod-crash-loop` | 1 | 0.270 | `out-of-memory` ✅ |

All five at rank 1, and in no case did the confusable runbook rank first.

**The confusable pairs matter more than the hit rate.** They are the cases where
a plausible-looking wrong answer would send an investigation in the wrong
direction — a payment-latency query surfacing `pod-crash-loop` would push the
agent toward looking for restarts that do not exist.

### Separately, storage behaviour is unit-tested

Integration tests use **deterministic vectors** rather than the real model, so
they run fast, need no Ollama, and assert on *ranking logic* rather than on how a
particular model happens to embed a sentence:

- re-indexing the same source updates rather than duplicating
- the closest match ranks first
- weak matches are excluded rather than padding results
- metadata filters are applied before similarity
- an incident never retrieves itself
- retrieval confidence is derived from distance and bounded

Whether the real model retrieves sensibly is the gold set's job. These two
questions are kept apart on purpose.

---

## The loop, demonstrated

Verified on live infrastructure:

```
Investigation 1
  Retrieved 0 past incident(s) and 5 runbook section(s)   ← memory empty
  → produced a report
  → Indexed incident ... into semantic memory

Investigation 2
  Retrieved 1 past incident(s) and 5 runbook section(s)   ← it learned
```

The platform accumulates its own history. In a long-running deployment that
history becomes the more useful half: runbooks describe failure modes in
general, past incidents describe how they actually manifested *here* — with the
real service names, the real thresholds, and the fix that actually worked.

### An operational note

With `OLLAMA_MAX_LOADED_MODELS=1`, an embedding request issued while the chat
model is working waits for that generation to finish **and** for a model swap —
measured at ~58 s to load the embedding model cold, on top of a generation that
can take 170 s.

Inside a normal investigation this never bites, because retrieval runs before
triage and the investigation owns the model slot for its whole duration. It only
appears when something external embeds concurrently, such as the gold-set
evaluation running while an investigation is in flight. Embedding timeouts are
300 s for that reason.
