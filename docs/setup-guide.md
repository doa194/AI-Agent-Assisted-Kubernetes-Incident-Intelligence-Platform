# Setup and operation

From nothing to a running, verified system — and how to live with it afterwards.

- [Prerequisites](#prerequisites)
- [Bootstrap](#bootstrap)
- [Completing the setup](#completing-the-setup)
- [Verifying it works](#verifying-it-works)
- [Day-to-day operation](#day-to-day-operation)
- [Watching it handle a failure](#watching-it-handle-a-failure)
- [Rebuilding after a change](#rebuilding-after-a-change)
- [Testing](#testing)
- [Stopping and cleaning up](#stopping-and-cleaning-up)
- [Starting over](#starting-over)

---

## Prerequisites

| Requirement | Minimum | Notes |
| --- | --- | --- |
| Docker Desktop | Running, 12 GB+ to containers | Less works, but slowly — see below |
| `kind` | v0.32+ | Node image is pinned, so the binary version matters little |
| `kubectl` | Any recent | Only used by the automation |
| Python | 3.11+ | Standard library only — no `pip install`, no venv |
| Free disk | ~25 GB | Models ~8 GB, images and cluster state make up the rest |
| .NET SDK | 10.0.303+ | **Only** to run tests; the platform builds in a container |

Check everything at once:

```bash
python kubesage.py preflight
```

```
==> Preflight checks
  OK docker found
  OK kind found
  OK kubectl found
  OK Docker daemon responding
  OK docker compose available
  OK Docker memory 13.6 GB
  OK NVIDIA container runtime detected - GPU acceleration will be enabled
  OK all required host ports are free
```

### About memory

The whole stack — a three-node cluster, six demo services, Loki, Prometheus,
Grafana, PostgreSQL and a 12B model — shares whatever Docker has.

| Docker memory | Experience |
| --- | --- |
| 16 GB+ | Comfortable |
| 12–16 GB | Works well; the default configuration is tuned for this |
| 8–12 GB | Works, but expect slow investigations and occasional model reloads |
| Under 8 GB | Not recommended |

Preflight warns rather than refuses. To raise it: Docker Desktop → Settings →
Resources, or `memory=` in `.wslconfig` on Windows.

### About the GPU

Optional. With an NVIDIA GPU and the container runtime available, part of the
model runs on the card and investigations are several times faster. Preflight
detects it and the automation adds the GPU overlay automatically.

Without one, everything still works on CPU — just slower. See
[configuration.md](configuration.md#no-gpu) for timeouts worth raising.

---

## Bootstrap

```bash
python kubesage.py bootstrap
```

**Expect 20–40 minutes on a first run**, almost entirely model download.

### What it does, in order

| Step | Why this order |
| --- | --- |
| 1. Preflight | Finding a taken port 20 minutes in is a poor experience |
| 2. Create the Kind cluster | The operations plane's config refers to ports that only exist once it is up |
| 3. Start PostgreSQL and Ollama | First, so ~8 GB downloads while nothing else needs the machine |
| 4. Pull models | `gemma4:12b` (~7.6 GB) and `embeddinggemma:300m` (~0.6 GB) |
| 5. Build and load workload images | Five services, built from one Dockerfile, pushed straight into the nodes |
| 6. Deploy the workload | Database first, then the services that depend on it |
| 7. Start the platform | Applies database migrations at startup |

Bootstrap is **safe to re-run**. An existing cluster is left alone, and models
already present are not re-downloaded — so an interrupted run resumes rather
than restarting.

### There is no registry

Images are built locally and pushed into the Kind nodes with
`kind load docker-image`, and the deployments use `imagePullPolicy: Never`. The
whole setup is offline-capable after the first bootstrap.

One exception: the upstream `postgres` image is preloaded on a best-effort basis
and Kubernetes pulls it if that fails. Multi-platform images from Docker Desktop
cannot always be exported in the single-platform form `kind load` expects, and
that is a warning rather than an error.

---

## Completing the setup

The observability stack and the platform's read-only identity are cluster
resources rather than part of the demo workload, so they are applied separately.

### Observability

```bash
kubectl --context kind-kubesage apply -f deploy/k8s/observability/
```

Creates the `kubesage-observability` namespace with Loki (single binary,
filesystem storage), a Fluent Bit DaemonSet on every node, and Prometheus with
annotation-driven discovery.

Wait for them:

```bash
kubectl --context kind-kubesage get pods -n kubesage-observability -w
```

### The read-only identity

```bash
kubectl --context kind-kubesage apply -f deploy/k8s/rbac/

python -c "import sys; sys.path.insert(0,'automation'); \
from kubesage.config import load_settings; from kubesage import rbac; \
rbac.generate_kubeconfig(load_settings())"
```

The first command creates the `kubesage-observer` service account with read-only
roles. The second mints its token and writes
`deploy/compose/generated/kubeconfig.yaml`, pointing at
`https://host.docker.internal:6443`.

That file contains a real credential, is mounted read-only into the platform
container, and is git-ignored.

Confirm the boundary holds — this asks the API server itself, not the YAML:

```bash
python -c "import sys; sys.path.insert(0,'automation'); \
from kubesage.config import load_settings; from kubesage import rbac; \
ok, problems = rbac.verify_read_only(load_settings()); \
print('read-only verified:', ok, problems)"
```

Then restart the platform so it picks up the mounted kubeconfig:

```bash
cd deploy/compose && docker compose --env-file ../../versions.env \
  -f docker-compose.yml -f docker-compose.gpu.yml up -d platform
```

---

## Verifying it works

```bash
python kubesage.py verify
```

Eighteen checks, each probing **real behaviour** rather than reading
configuration:

```
==> Verifying the operations plane
  OK compose services: 4 running
  OK postgres + pgvector: pgvector 0.8.6
  OK database least privilege: application role cannot alter the schema
  OK kubesage platform ready: readiness reports Healthy
  OK ollama models: gemma4:12b, embeddinggemma:300m
  OK embedding model: 768 dimensions
  OK chat model loads and responds: replied 'ready'
==> Verifying the cluster
  OK kind cluster: 3 nodes Ready (kubesage-control-plane=control-plane, ...)
  OK operations plane -> cluster network: container can reach the cluster's published ports
  OK demo workload: 8 pods Running and Ready
  OK automatic traffic: 407 requests in the last 5 minutes
==> Verifying the telemetry pipeline
  OK log pipeline: labels ['container', 'job', 'level', 'namespace'], 16 containers shipping logs
  OK prometheus scraping: 8 healthy scrape targets
  OK grafana: reachable with provisioned Loki and Prometheus datasources
  OK evidence api: 7 correlated items across ['KubernetesState', 'Metric']
==> Verifying semantic memory
  OK semantic memory: 25 runbook section(s), 5 remembered incident(s)
==> Semantic retrieval evaluation (5 gold cases, top-5)
  OK payment latency: 'dependency-latency' at rank 1 (distance 0.329)
  OK container out of memory: 'out-of-memory' at rank 1 (distance 0.269)
  OK database unavailable: 'database-unavailable' at rank 1 (distance 0.285)
  OK readiness probe failing: 'readiness-failure' at rank 1 (distance 0.232)
  OK crash loop: 'pod-crash-loop' at rank 1 (distance 0.270)
    5/5 gold retrieval cases passed
  OK semantic retrieval quality: 5 gold cases
==> Verifying security boundaries
  OK read-only kubernetes rbac: reads allowed; mutations, secrets, exec and port-forward all denied
==> Verification summary
    18 checks, 18 passed, 0 warning(s), 0 failure(s)
  OK environment verified
```

Counts vary with how long the environment has been running — request volume,
evidence item count and remembered incidents all grow. The check names and the
`18 checks, 18 passed` summary are what should be stable.

Some of these are worth understanding:

**"database least privilege"** actually runs `CREATE TABLE` as the application
role and expects it to be refused.

**"operations plane → cluster network"** is the single most important
environmental check. If this path is broken, every telemetry query fails at
runtime with an error that looks like a Loki problem rather than a networking
one.

**"log pipeline"** asserts the four intended labels are present *and* that
high-cardinality names are absent. Checking only for presence would let a
regression add `correlationId` unnoticed.

**"semantic retrieval quality"** scores a gold set — whether the *right* runbook
is retrieved, not merely whether something is.

---

## Day-to-day operation

```bash
python kubesage.py status
```

```
==> Operations plane (Docker Compose)
    kubesage-platform        running      health=healthy
    kubesage-ollama          running      health=healthy
    kubesage-postgres        running      health=healthy
    kubesage-grafana         running      health=healthy
==> Cluster (Kind)
    kubesage-control-plane           Ready=True   role=control-plane
    kubesage-worker                  Ready=True   role=worker
    kubesage-worker2                 Ready=True   role=worker
==> Endpoints
    [ up ] KubeSage API           http://127.0.0.1:8081/health/ready
    [ up ] Grafana                http://127.0.0.1:3000/api/health
    [ up ] Loki                   http://127.0.0.1:3100/ready
    [ up ] Prometheus             http://127.0.0.1:9090/-/healthy
    [ up ] Demo gateway           http://127.0.0.1:8080/health/ready
    [ up ] Ollama                 http://127.0.0.1:11434/api/tags
==> Dependency health (including degradations readiness hides)
    [       ok] database     PostgreSQL reachable, pgvector 0.8.6.
    [       ok] telemetry    All telemetry sources reachable.
    [       ok] model        Ollama is reachable.
==> Useful addresses
    Grafana dashboards   http://127.0.0.1:3000 (anonymous viewer access)
    KubeSage incidents   http://127.0.0.1:8081/incidents
    Latest report        http://127.0.0.1:8081/reports/latest
```

The **dependency health** section is the one to read when something feels wrong.
"Ready" and "fully working" are different states: with Ollama down the platform
is correctly *Ready* — it still detects and records incidents — but
investigations are only being queued. Readiness alone will not show that.

### Start and stop

```bash
python kubesage.py stop     # stop the operations plane, keep all data
python kubesage.py start    # bring it back
```

`stop` deliberately leaves the Kind cluster running. Stopping and starting Kind
nodes is slow and error-prone, and leaving them up costs little.

### What runs on its own

Once bootstrapped, nothing waits for you:

| Trigger | Cadence | Produces |
| --- | --- | --- |
| Startup analysis | Once, after a 120s warm-up | A cluster health report |
| Detection | Every 60s | Incidents, when rules fire |
| Scheduled analysis | Every 300s | A cluster health report |
| Investigation | Whenever work is queued | An incident report |

---

## Watching it handle a failure

```bash
python kubesage.py scenario list
```

Five scenarios; `payment-latency` is the most instructive because the obvious
answer is wrong.

```bash
python kubesage.py scenario run payment-latency
```

### Follow it through

**Detection**, within about 90 seconds:

```bash
curl -s http://127.0.0.1:8081/incidents | python -m json.tool
```

**Evidence**, collected with no AI involvement:

```bash
curl -s "http://127.0.0.1:8081/evidence?workload=order-api&windowMinutes=5" \
  | python -m json.tool
```

**Queue depth**, to see the investigation being picked up:

```bash
curl -s http://127.0.0.1:8081/cluster/status
```

**The agents**, in the platform logs:

```bash
docker logs -f kubesage-platform 2>&1 | grep -o '"Message":"[^"]*"'
```

```
"Agent triage completed in 106s"
"Agent investigation completed in 138s"
"Agent report completed in 166s"
"Investigation finished in 412s with state Reported"
"Incident report generated ... root cause: performance degradation in payment-simulator"
```

**The report**:

```bash
curl -s http://127.0.0.1:8081/reports/latest | python -m json.tool
```

**The evidence behind it** — this is the part that matters:

```bash
REPORT_ID=$(curl -s http://127.0.0.1:8081/reports/latest | python -c "import json,sys; print(json.load(sys.stdin)['id'])")
curl -s "http://127.0.0.1:8081/reports/$REPORT_ID/evidence" | python -m json.tool
```

Each cited item includes the exact query that produced it. Paste one into
Grafana at `http://127.0.0.1:3000` and you see the same data the agent saw.

### Always reset

```bash
python kubesage.py scenario reset payment-latency
python kubesage.py scenario reset all      # clear every fault
```

A fault left active contaminates every later run and looks like a new incident.

---

## Rebuilding after a change

### The demo services

```bash
python kubesage.py workload
```

Rebuilds all five images, loads them into the cluster nodes, reapplies the
manifests and restarts the deployments. The restart is necessary because
Kubernetes does not notice a new image behind the same tag.

### The platform

```bash
cd deploy/compose
docker compose --env-file ../../versions.env \
  -f docker-compose.yml -f docker-compose.gpu.yml up -d --build platform
```

Omit `-f docker-compose.gpu.yml` without an NVIDIA GPU. Migrations run
automatically at startup.

### Observability or RBAC

```bash
kubectl --context kind-kubesage apply -f deploy/k8s/observability/
kubectl --context kind-kubesage rollout restart deployment/loki -n kubesage-observability
```

Changing a ConfigMap does not restart the pod that mounts it, so a rollout
restart is needed for the new config to take effect.

---

## Testing

```bash
dotnet test                                  # 96 unit + 25 integration + 11 API
python kubesage.py verify                    # 18 operational checks
python kubesage.py scenario check all        # all five scenarios (~20 min)
python kubesage.py e2e                       # two critical workflows (~30 min)
```

`dotnet test` needs Docker running — integration and API tests start throwaway
PostgreSQL containers — but does **not** need the cluster or Ollama.

`scenario check all` and `e2e` both use the real model and take a long time.
Run them one at a time: running them concurrently saturates a single machine and
can make the Kubernetes API briefly unresponsive.

See [testing-strategy.md](testing-strategy.md) for what each layer protects.

---

## Stopping and cleaning up

| Command | Removes | Keeps |
| --- | --- | --- |
| `python kubesage.py stop` | nothing | everything |
| `python kubesage.py cleanup --keep-models` | cluster, containers, database | the ~8 GB model volume |
| `python kubesage.py cleanup` | everything | nothing |

Use `--keep-models` unless you actually want to re-download eight gigabytes. The
next bootstrap is then only a few minutes.

Cleanup removes the Kind cluster, the Compose containers and volumes, and the
`.kubesage-build/` scratch directory.

---

## Starting over

To reset just the platform's data, keeping the cluster and models:

```bash
docker exec kubesage-postgres psql -U kubesage_owner -d kubesage \
  -c "TRUNCATE incidents CASCADE; TRUNCATE work_items; DELETE FROM reports;"
```

This clears incidents, evidence, investigations, reports and queued work.
Runbook embeddings survive, since re-indexing them costs model time and they are
static input rather than run state.

For a completely fresh environment:

```bash
python kubesage.py cleanup --keep-models
python kubesage.py bootstrap
```

If bootstrap fails partway and leaves orphaned Kind containers that
`kind delete` cannot find:

```bash
docker rm -f $(docker ps -aq --filter "label=io.x-k8s.kind.cluster=kubesage")
```

More recovery procedures in [troubleshooting.md](troubleshooting.md).
