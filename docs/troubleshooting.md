# Troubleshooting

- [Start here](#start-here)
- [Bootstrap problems](#bootstrap-problems)
- [The platform will not start or stay healthy](#the-platform-will-not-start-or-stay-healthy)
- [Detection is not producing incidents](#detection-is-not-producing-incidents)
- [Investigations are not running](#investigations-are-not-running)
- [Investigations run but produce poor results](#investigations-run-but-produce-poor-results)
- [Telemetry problems](#telemetry-problems)
- [Semantic memory problems](#semantic-memory-problems)
- [Scenario problems](#scenario-problems)
- [Host resource exhaustion](#host-resource-exhaustion)
- [Performance expectations](#performance-expectations)

---

## Start here

```bash
python kubesage.py status     # what is running, and dependency health
python kubesage.py verify     # 18 checks that probe real behaviour
```

`status` includes a **dependency health** section showing every check, including
ones excluded from readiness. That is where a degradation appears — with Ollama
down the platform is correctly *Ready*, but investigations are only being
queued.

Useful raw views:

```bash
docker logs kubesage-platform 2>&1 | grep -o '"Message":"[^"]*"' | tail -30
docker logs kubesage-platform 2>&1 | grep '"LogLevel":"Error"' | tail -5
curl -s http://127.0.0.1:8081/health/detail | python -m json.tool
curl -s http://127.0.0.1:8081/cluster/status
```

---

## Bootstrap problems

### Preflight reports ports in use

Usually a previous run:

```bash
python kubesage.py cleanup --keep-models
```

If something else on the machine owns the port, change it in `versions.env` —
every component reads the value from there. The Kind node ports in
`deploy/kind/cluster.yaml` must be kept in step manually.

### `container name "kubesage-worker2" is already in use`

An interrupted `kind create` left partial containers that `kind delete` cannot
find, because the cluster was never fully registered:

```bash
docker rm -f $(docker ps -aq --filter "label=io.x-k8s.kind.cluster=kubesage")
python kubesage.py bootstrap
```

### PostgreSQL restarts in a loop mentioning the data directory

PostgreSQL 18 images store data in a version-specific subdirectory, so the
volume must be mounted at `/var/lib/postgresql`, not `/var/lib/postgresql/data`.
If an older volume exists:

```bash
cd deploy/compose
docker compose --env-file ../../versions.env down
docker volume rm kubesage_postgres-data
```

### The model download is slow or interrupted

`gemma4:12b` is about 7.6 GB. Pulls resume, so re-running bootstrap continues
rather than restarting.

```bash
docker exec kubesage-ollama ollama list      # what has arrived
docker exec kubesage-ollama ollama pull gemma4:12b
```

### `kind load docker-image` fails for the postgres image

```
ctr: content digest sha256:... not found
```

Expected and harmless. Multi-platform images pulled by Docker Desktop cannot
always be exported in the single-platform form `kind load` expects. Preloading
is only an optimisation — Kubernetes pulls the image itself.

---

## The platform will not start or stay healthy

### It exits immediately with a configuration message

Configuration is validated at startup, and the message names both values and the
consequence:

```
Investigation.WorkLeaseSeconds (900) must be at least
Investigation.TimeoutSeconds (1800); a shorter lease allows a running
investigation to be claimed twice.
```

Fix the setting rather than working around the check. See
[configuration.md](configuration.md#validation).

### Readiness fails with a database error

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "SELECT extversion FROM pg_extension WHERE extname='vector'"
```

A missing extension usually means the volume was created before the init script
existed. Remove the volume and let it re-initialise.

### `data type name 'vector' could not be found`

Npgsql reads the database's type catalogue once and caches it. If the data
source connects before pgvector exists, it never learns the type.

The platform calls `ReloadTypesAsync` after migrations to prevent this, so
seeing it means something connected earlier than expected. Restarting the
platform resolves it.

### Kubernetes evidence is empty, or the client cannot connect

```bash
grep server deploy/compose/generated/kubeconfig.yaml
# should read https://host.docker.internal:6443
```

If missing, regenerate:

```bash
python -c "import sys; sys.path.insert(0,'automation'); \
from kubesage.config import load_settings; from kubesage import rbac; \
rbac.generate_kubeconfig(load_settings())"
```

Then restart the platform so it re-reads the mount. If the file exists but TLS
fails, the API server certificate may lack the `host.docker.internal` SAN —
recreate the cluster, since that is set in the kind config.

---

## Detection is not producing incidents

Work through these in order.

**1. Is traffic flowing?** Detection needs a sample.

```bash
curl -s 'http://127.0.0.1:9090/api/v1/query?query=sum(increase(kubesage_http_requests_total[5m]))'
```

Below `MinimumRequestSample` (20) in the window, ratio-based rules correctly
refuse to fire.

**2. Is Prometheus scraping?** `python kubesage.py verify` reports the target
count; `http://127.0.0.1:9090/targets` shows which.

**3. Has enough time passed?** Rules evaluate a five-minute window every minute.
Most scenarios need 60–120 seconds to appear.

**4. Force a pass:**

```bash
curl -X POST http://127.0.0.1:8081/analysis/run
```

**5. Is detection enabled?** `KubeSage:Detection:Enabled`. The platform logs a
warning at startup if not.

### Too many incidents from one failure

Expected to a degree — one outage legitimately trips several rules. Suppression
reduces a database outage from twelve candidates to four.

```bash
docker logs kubesage-platform 2>&1 | grep -o '"Message":"Suppressed[^"]*"'
```

If you see many more, check whether a rule is including a volatile value in its
fingerprint, which would give each observation a new identity.

### The same incident keeps reappearing

`DeduplicationCooldownMinutes` must be at least `EvaluationWindowMinutes`.
Validation enforces this at startup, so if the platform started, the fingerprint
is probably changing between passes.

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "SELECT fingerprint, category, count(*), max(occurrence_count)
      FROM incidents GROUP BY 1,2 ORDER BY 3 DESC LIMIT 10"
```

Many rows with the same category but different fingerprints confirms it.

---

## Investigations are not running

```bash
curl -s http://127.0.0.1:8081/cluster/status
```

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Pending` items, nothing progressing | Ollama unreachable | Check `/health/detail`; the dispatcher logs `releasing N claimed work item(s)` |
| One `Claimed`, others waiting | Working normally | `MaxConcurrent` is 1 by default |
| Items in `Failed` | Retries exhausted | See the error below |
| Queue empty but incidents open | Work never enqueued | See stranded incidents below |

### Inspect failed work

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "SELECT kind, attempt, left(last_error, 160) FROM work_items WHERE state='Failed'"
```

### Work items complete instantly without doing anything

A queue draining while nothing happens looks identical to a healthy one. This
was a real defect: the payload was written camelCase and read case-sensitively,
so it bound to a default value and the dispatcher found no incident id.

An unreadable payload now **throws** rather than skipping, so it surfaces as a
failed item with the payload in the error. If you see that error, the payload
shape and the consuming record have diverged.

### Incidents open with no queued work

Startup recovery should catch this. It runs about ten seconds after startup:

```bash
docker logs kubesage-platform 2>&1 | grep -o '"Message":"Startup recovery[^"]*"'
```

```
Startup recovery complete: 7 unfinished incident(s), 0 requeued,
7 already had queued work
```

`0 requeued` with all incidents already having work is the healthy case.
Restarting the platform re-runs recovery.

---

## Investigations run but produce poor results

### An agent call times out

Check what the model is actually doing:

```bash
docker exec kubesage-ollama ollama ps
docker logs kubesage-ollama 2>&1 | grep "prompt processing" | tail -3
```

Look at the **CPU/GPU split** and the **prompt processing rate**. Healthy on a
GTX 1060 is roughly 49% CPU / 51% GPU. If more is on the CPU:

| Cause | Fix |
| --- | --- |
| A second model is resident | `OLLAMA_MAX_LOADED_MODELS=1` — this collapsed prompt processing to 14 tokens/sec in testing |
| Context window too large | The KV cache competes with model weights; 16K → 8K took triage from 600 s to 106 s |
| Another process using the GPU | Check `nvidia-smi` |

### Reports come back inconclusive

A legitimate outcome, not a failure. Common causes:

**The evidence genuinely does not distinguish** between causes. Check what was
collected:

```bash
curl -s "http://127.0.0.1:8081/evidence?workload=<name>&windowMinutes=15"
```

**Telemetry was unavailable** — check `unavailable_sources`:

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "SELECT state, evidence_complete, unavailable_sources FROM investigations
      ORDER BY started_at_utc DESC LIMIT 5"
```

**All hypotheses were rejected in validation**, because they cited evidence that
does not exist. This is the validator doing its job:

```bash
docker logs kubesage-platform 2>&1 | grep -o '"Message":"[^"]*non-existent evidence[^"]*"'
```

### The report blames the wrong workload

Check whether the discriminating evidence was actually present. For a dependency
problem the key comparison is per-dependency latency:

```bash
curl -s "http://127.0.0.1:8081/evidence?workload=order-api" \
  | python -c "import json,sys; [print(i['summary']) for i in json.load(sys.stdin)['items'].get('Metric',[])]"
```

If only one dependency appears, the agent had nothing to compare against. That
is an evidence problem, not a reasoning problem.

### An incident is stuck in `Investigating`

It should not be able to be — a safety net converts any non-terminal end state
to `Failed`, and startup recovery requeues unfinished incidents. If you see one
stuck, confirm the platform is running, then restart it.

---

## Telemetry problems

### Loki has no logs

```bash
curl -s http://127.0.0.1:3100/loki/api/v1/label/container/values
```

Expect `gateway`, `order-api`, `payment-simulator` and others. If empty:

```bash
kubectl --context kind-kubesage get ds -n kubesage-observability
kubectl --context kind-kubesage logs -n kubesage-observability -l app.kubernetes.io/name=fluent-bit --tail=50
```

A Fluent Bit config error keeps the pod running while shipping nothing.

### Loki labels are wrong

Expected exactly: `container`, `job`, `level`, `namespace`.

Seeing `kubernetes_container_name` means the flatten-and-rename filters are not
applying — the Loki output plugin names a label after the record path it came
from. Seeing `service_name` or `detected_level` means Loki's automatic discovery
is not disabled.

### Prometheus has no application metrics

Discovery is annotation-driven:

```bash
kubectl --context kind-kubesage get pod -n kubesage-demo -o json \
  | grep -c "prometheus.io/scrape"
```

Then check `http://127.0.0.1:9090/targets` for scrape errors.

### Loki crash-loops on startup

Usually an invalid config key. The message names the line:

```bash
kubectl --context kind-kubesage logs -n kubesage-observability -l app.kubernetes.io/name=loki --tail=20
```

Remember a ConfigMap change does not restart the pod that mounts it:

```bash
kubectl --context kind-kubesage rollout restart deployment/loki -n kubesage-observability
```

---

## Semantic memory problems

### No runbooks indexed

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "SELECT kind, count(*) FROM semantic_memory GROUP BY kind"
```

Expect 25 runbook sections. If zero:

```bash
docker logs kubesage-platform 2>&1 | grep -o '"Message":"[^"]*runbook[^"]*"'
```

Indexing waits for the embedding model and gives up after ten attempts, logging
a warning. Restarting the platform retries.

### Retrieval returns nothing

Two likely causes:

**The distance cut-off** is excluding everything. Working as designed if the
matches are genuinely poor — verify with the gold set:

```bash
python kubesage.py verify     # includes the retrieval evaluation
```

**Embedding is timing out.** With one model slot, an embed request during a
generation waits for that generation *plus* a ~58 s model swap:

```bash
curl -s -w "\n%{time_total}s\n" --max-time 300 \
  http://127.0.0.1:11434/api/embed \
  -d '{"model":"embeddinggemma:300m","input":"test"}' | tail -1
```

A `load_duration` of tens of seconds confirms a cold load. Timeouts are 300 s
for this reason; if you shortened them, that is the cause.

---

## Scenario problems

### A scenario does not produce its expected signals

Reset everything first — a leftover fault from an interrupted run is the usual
cause:

```bash
python kubesage.py scenario reset all
```

Then confirm the fault env var is actually set:

```bash
kubectl --context kind-kubesage get deploy -n kubesage-demo -o json \
  | grep -o 'KUBESAGE_FAULT[A-Z_]*'
```

### After `database-unavailable`, services stay broken

They cached a dead connection pool:

```bash
kubectl --context kind-kubesage rollout restart \
  deployment/order-api deployment/notification-worker -n kubesage-demo
```

The scenario check does this automatically.

### `OOMKilled` does not happen

The allocation must exceed the container limit **and** be unmanaged memory — the
.NET GC honours the cgroup limit and would throw inside the process instead.

```bash
kubectl --context kind-kubesage get deploy payment-simulator -n kubesage-demo \
  -o jsonpath='{.spec.template.spec.containers[0].resources.limits.memory}'
# expect 192Mi
```

### A crash scenario shows no log evidence

The crashing container's last words are in the **previous** instance:

```bash
kubectl --context kind-kubesage logs -n kubesage-demo \
  -l app.kubernetes.io/name=order-api --previous --tail=50
```

---

## Host resource exhaustion

Running everything at once on a constrained machine can saturate the host. The
symptom escalates:

```
kubectl: Unable to connect to the server: net/http: TLS handshake timeout
   ↓
docker ps: request returned 500 Internal Server Error
   ↓
Docker daemon unresponsive
```

The automation retries transient API failures, but sustained saturation is not
transient.

### Avoiding it

- **Do not run `scenario check all` and `e2e` concurrently.** Each drives real
  investigations; together they overload a single machine.
- Let one finish before starting the next.
- Investigations are already limited to one at a time; the load comes from
  running *harnesses* in parallel.

### Recovering

Restart Docker Desktop. Compose containers with `restart: unless-stopped` and
the Kind nodes come back on their own. Then:

```bash
python kubesage.py status
python kubesage.py scenario reset all     # clear anything left applied
python kubesage.py verify
```

---

## Performance expectations

So that "slow" is not mistaken for "broken". GTX 1060 6GB, 13.6 GB Docker
memory:

| Operation | Expected | Suspicious above |
| --- | --- | --- |
| Model cold load | ~34 s | 90 s |
| Triage agent | 80–110 s | 300 s |
| Investigation agent | 110–140 s | 400 s |
| Report agent | 110–170 s | 400 s |
| **Full investigation** | **~7 min** | 15 min |
| Detection pass | 1–3 s | 30 s |
| Embedding, warm | < 1 s | 5 s |
| Embedding, cold swap | ~58 s | 120 s |
| Evidence bundle | 2–5 s | 30 s |

Generation runs at roughly **6 tokens/sec** with a 49% CPU / 51% GPU split.

If an investigation takes 20+ minutes the model has almost certainly lost GPU
residency — `docker exec kubesage-ollama ollama ps` shows the split.
