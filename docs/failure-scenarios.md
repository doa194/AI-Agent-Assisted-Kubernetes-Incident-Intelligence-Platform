# Failure scenarios

Five reproducible failures, each producing a distinct evidence signature.

```bash
python kubesage.py scenario list
python kubesage.py scenario run <name>
python kubesage.py scenario reset <name>
python kubesage.py scenario reset all
python kubesage.py scenario check all       # run each end to end and verify
```

- [How injection works](#how-injection-works)
- [The five scenarios](#the-five-scenarios)
- [Telling them apart](#telling-them-apart)
- [Ground truth is private](#ground-truth-is-private)
- [Verifying a scenario](#verifying-a-scenario)
- [Scoring an investigation](#scoring-an-investigation)

---

## How injection works

Every scenario is applied by changing a Kubernetes deployment — setting an
environment variable, or changing the replica count — and reset by undoing
exactly that change.

Nothing reaches into a running container, and no service exposes an endpoint
that can break it.

| Benefit | Why it matters |
| --- | --- |
| The change is a real Kubernetes event | The evidence an investigator sees is genuine cluster activity, not simulated |
| Reset is precise | It is the exact inverse of one declarative edit |
| No destructive endpoint exists | A `/control/break` route in every service would be far easier and much worse |

Faults are read from environment variables **once at startup**
(`FaultSettings.FromEnvironment`), so activating one triggers a normal rolling
update. An unparsable value means "no fault" rather than a crash — a typo in a
scenario definition should leave the workload healthy, not break it in a way
that looks like a real incident.

---

## The five scenarios

### `app-crash` — application crash loop

**Applies:** `KUBESAGE_FAULT_CRASH_AFTER_SECONDS=25` on **order-api**

The process starts normally, serves traffic for 25 seconds, then exits with a
failure code via `Environment.FailFast`. Kubernetes restarts it, and after a few
rounds the pod enters `CrashLoopBackOff`.

**The delay is deliberate.** A pod that fails during startup produces very
different evidence from one that serves traffic and then dies, and the second is
the more interesting case to investigate.

`FailFast` rather than `Environment.Exit` because the latter runs shutdown
handlers and can exit cleanly with code 0, producing no restart evidence worth
looking at.

| Evidence | Where |
| --- | --- |
| Restart count climbing, `CrashLoopBackOff` | Kubernetes pod state |
| `BackOff` warning events | Kubernetes events |
| `Simulated unrecoverable failure` | Logs — in the **previous** container instance |
| Gateway 503s naming order-api as the dependency | Logs and metrics |

---

### `payment-latency` — slow downstream dependency

**Applies:** `KUBESAGE_FAULT_LATENCY_MS=3000` on **payment-simulator**

Three seconds, past the order API's 2-second HTTP timeout.

**This is the most important scenario**, because the obvious answer is wrong.
Errors appear on order-api and the gateway; **neither is broken**. The service
actually causing the problem looks healthy — it answers every request, just
slowly. And nothing in Kubernetes looks wrong at all.

| Evidence | Where |
| --- | --- |
| Elevated 5xx on order-api and gateway | Metrics |
| p95 latency flat at the timeout value | Metrics — every affected request fails at the same deadline |
| `timeout` failures naming payment-simulator | `kubesage_dependency_failures_total` |
| `Dependency payment-simulator timed out after 2001ms` | Logs |
| **No pod restarts anywhere** | Kubernetes — the absence *is* the evidence |

**The discriminator** is comparing one service's dependencies:

```
order-api → payment-simulator   4.821s
order-api → workload-database   0.009s
```

---

### `database-unavailable` — shared dependency down

**Applies:** scales **workload-db** to zero replicas

Both order-api and notification-worker lose their storage at once.

| Evidence | Where |
| --- | --- |
| Connection failures, **not** timeouts | Logs — a refused connection returns immediately |
| Two independent services failing on one dependency | Metrics, split by `kind=connection` |
| Deployment showing 0 ready replicas | Kubernetes |
| The worker silently making no progress | `kubesage_notifications_pending` rising |

Two unrelated callers failing against the same dependency is much stronger
evidence than either alone — and the detection rule raises severity for exactly
that reason.

The worker case is instructive: it keeps running and looks healthy to
Kubernetes while doing nothing useful. Only the business metric shows it.

**Reset needs a follow-up.** Dependent services cache a dead connection pool, so
the check restarts them; manually:

```bash
kubectl --context kind-kubesage rollout restart \
  deployment/order-api deployment/notification-worker -n kubesage-demo
```

---

### `readiness-failure` — unready but not restarting

**Applies:** `KUBESAGE_FAULT_UNREADY=true` on **notification-worker**

The readiness probe fails while the process keeps running, so Kubernetes removes
the pod from Service endpoints without restarting it.

| Evidence | Where |
| --- | --- |
| Pod `Running` but not `Ready` | Kubernetes pod state |
| **Restart count unchanged** | Kubernetes |
| `Unhealthy` warning events | Kubernetes events |
| `Readiness probe is failing: readiness fault injected` | Logs |

**The distinguishing pair is readiness together with restart count:**

- not ready + **zero** restarts → a readiness problem
- not ready + **rising** restarts → a crash loop

They need completely different fixes, and conflating them sends the
investigation looking for an exception that does not exist.

Liveness deliberately stays healthy. This scenario is about being removed from
the Service, not about being restarted — conflating the two would destroy the
evidence that makes it distinguishable.

> The readiness log line was added because of this scenario. Originally the
> reason existed only in the HTTP 503 body, and the platform reads logs, metrics
> and cluster state — never HTTP bodies. A readiness failure therefore produced
> **no log evidence at all**, leaving an investigation with "not ready" and no
> explanation. It is throttled to once a minute so an unready pod explains
> itself often enough to land in a detection window without flooding Loki.

---

### `oom-kill` — memory limit exceeded

**Applies:** `KUBESAGE_FAULT_ALLOCATE_MB=512` on **payment-simulator**, whose
container limit is 192Mi

| Evidence | Where |
| --- | --- |
| `OOMKilled` with **exit code 137** | Kubernetes — conclusive, nothing else produces this |
| Restart count rising | Kubernetes |
| Memory working set converging on the limit | `container_memory_working_set_bytes` |
| **No application error at the moment of death** | Logs — informative by its absence |

**The allocation is unmanaged memory, deliberately.** The .NET garbage collector
honours the container limit and would throw `OutOfMemoryException` inside the
process, which Kubernetes reports as an ordinary crash. Allocating outside the
managed heap with `Marshal.AllocHGlobal` **and touching every page** forces the
kernel's OOM killer, which is what produces a genuine `OOMKilled`.

Writing to the memory matters: merely allocating it would not cause the kernel
to back it with physical pages.

The absence of an application error is itself diagnostic. A process that logged
an exception and exited is a crash; one that vanished without warning was
killed.

---

## Telling them apart

The reason there are five is that each has a signature no other produces. This
table is what the investigation agent is effectively being asked to reproduce:

| Signal | app-crash | payment-latency | database-unavailable | readiness-failure | oom-kill |
| --- | --- | --- | --- | --- | --- |
| Restart count rising | ✅ | ❌ | ❌ | ❌ | ✅ |
| `CrashLoopBackOff` | ✅ | ❌ | ❌ | ❌ | ✅ |
| `OOMKilled` / exit 137 | ❌ | ❌ | ❌ | ❌ | ✅ |
| Pod not Ready | ✅ | ❌ | ❌ | ✅ | ✅ |
| Dependency **timeouts** | ❌ | ✅ | ❌ | ❌ | ❌ |
| Dependency **connection failures** | ❌ | ❌ | ✅ | ❌ | ❌ |
| Multiple independent callers affected | ❌ | ❌ | ✅ | ❌ | ❌ |
| Application error at time of death | ✅ | n/a | n/a | n/a | ❌ |
| Kubernetes looks entirely healthy | ❌ | ✅ | ❌ | ❌ | ❌ |

The two rows that carry the most diagnostic weight are **restart count** and
**timeout versus connection failure**. Between them they separate four of the
five.

---

## Ground truth is private

Each scenario has expected-outcome metadata in
`automation/kubesage/scenarios/ground_truth.py`:

```python
"payment-latency": ExpectedOutcome(
    incident_category="dependency_latency",
    affected_workloads=["order-api", "gateway"],
    root_cause_workload="payment-simulator",       # NOT the same thing
    root_cause_category="downstream_dependency_slow",
    expected_log_substrings=["timed out", "payment-simulator"],
    expect_kubernetes_disruption=False,
    incorrect_root_cause_workloads=["order-api", "gateway", "workload-db"],
)
```

**The agents never see any of it.** The boundary is enforced by construction,
not by intention:

| Enforcement | How |
| --- | --- |
| Not in the container image | The platform image is built from `src/` and `knowledge/` only |
| Not sent over the wire | The automation reads results from the public API; it never sends expectations |
| Not in any data store | Nothing here is written to Loki, Prometheus, the database or the runbook corpus |

If any of it were visible to the agents, the investigation results would prove
nothing at all.

It is used by exactly two things: the operational check that a scenario really
produced the telemetry it should, and the evaluation that scores a finished
report afterwards.

---

## Verifying a scenario

```bash
python kubesage.py scenario check all
```

### How long before signals appear

Each scenario declares a `signal_delay_seconds` in
`automation/kubesage/scenarios/definitions.py` — how long after injection the
telemetry should be visible. `scenario run` prints it, and `scenario check`
waits exactly that long before asserting.

| Scenario | Wait | Why this long |
| --- | --- | --- |
| `app-crash` | 120 s | Needs several restart cycles before `CrashLoopBackOff` |
| `oom-kill` | 120 s | Allocation, kill and restart all have to complete |
| `payment-latency` | 90 s | Enough requests must fail to move a 5-minute ratio |
| `database-unavailable` | 90 s | Connection failures accumulate quickly |
| `readiness-failure` | 75 s | The fastest — the probe fails on the next poll |

Nothing is broken if a signal has not appeared sooner; these are the windows the
rules need, not how long Kubernetes takes to act.

### What is asserted

Each scenario is run end to end and asserted on three things:

1. **it produces the telemetry it is supposed to**
2. **that telemetry is distinct enough to tell scenarios apart**
3. **reset returns the workload to a healthy state**

Point 3 is easy to skip and expensive to get wrong. A scenario that does not
reset cleanly quietly contaminates every later run, and the symptom appears as a
mysteriously failing test somewhere else entirely.

For `payment-latency` the check also asserts the **absence** of Kubernetes
disruption. If pods are restarting, the scenario is not reproducing the
dependency-latency situation it claims to, and any conclusion drawn from it
would be measuring the wrong thing.

### Details the harness has to get right

These were all found by getting them wrong first:

**A crashed container's logs are in the previous instance.** `kubectl logs`
shows the fresh container, which has not said anything yet. The check reads both.

**Applying a scenario restarts pods.** `set env` triggers a rolling update, so
the check waits for the rollout to finish and takes a restart-count baseline
*before* observing. Otherwise the injection mechanism itself is counted as
disruption.

**A stale termination reason is not evidence.** A pod carrying `Unknown` from an
unrelated Docker restart hours earlier was read as current disruption. A
last-termination reason now only counts when the pod restarted since the
baseline.

**Readiness has two faces.** It surfaces as a `NotReady` pod *and* an `Unhealthy`
event. Either satisfies the check.

### An alarming line that is not a failure

Injecting `readiness-failure` prints this partway through:

```
error: timed out waiting for the condition
```

That is `kubectl rollout status` giving up, and it is **correct** — the whole
point of the scenario is a pod that runs but never becomes ready, so the rollout
genuinely cannot complete. The harness expects it and carries on. Judge the run
by the summary block, not by this line.

### The summary

```
==> Scenario check summary
    [PASS] app-crash              pod condition Error; expected log evidence present
    [PASS] payment-latency        expected log evidence present; no Kubernetes disruption, as expected
    [PASS] database-unavailable   expected log evidence present
    [PASS] readiness-failure      pod condition Unhealthy; expected log evidence present
    [PASS] oom-kill               pod condition OOMKilled; expected log evidence present
```

Expected runtime: about 20 minutes for all five. The command exits non-zero if
any scenario fails, so it is safe to use as a gate.

---

## Scoring an investigation

After a scenario, a finished report is scored against the private ground truth:

| Check | Catches |
| --- | --- |
| Names the true root cause workload | The central claim |
| Root cause category is the right kind of problem | Wrong diagnosis with right-sounding words |
| Does not blame a victim workload | A report that stopped at the symptom |
| Every citation resolves to stored evidence | Fabricated grounding |
| Does not invent a pod failure when nothing restarted | Misread evidence |

Categories are compared by **shared significant words**, not exact strings.
Models phrase them differently between runs, and asserting on exact wording
would test the phrasing rather than the diagnosis — `dependency_latency` and
`downstream_dependency_slow` describe the same finding.

### A measured result

`payment-latency`, scored 5/5:

```
[PASS] names the true root cause workload 'payment-simulator'
[PASS] root cause category is the right kind of problem
       reported 'dependency_latency', expected 'downstream_dependency_slow'
[PASS] does not blame a workload that was only a victim
[PASS] every cited evidence id resolves to real stored evidence — 6/6
[PASS] does not invent a pod failure (nothing restarted in this scenario)
```

The incident was raised against **order-api**. The report correctly identified
**payment-simulator**, and inferred the 2-second timeout threshold from log
evidence showing timeouts at 2001ms.
