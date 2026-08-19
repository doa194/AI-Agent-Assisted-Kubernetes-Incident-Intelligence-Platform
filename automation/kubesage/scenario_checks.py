"""Operational verification that each failure scenario really works.

For every scenario this proves three things end to end:

  1. applying it produces the telemetry it is supposed to produce,
  2. that telemetry is distinct enough to tell scenarios apart,
  3. resetting it returns the workload to a healthy state.

Point 3 is the one that is easy to skip and expensive to get wrong. A scenario
that does not reset cleanly quietly contaminates every later run, and the
symptoms show up as a mysteriously failing test somewhere else.

This module reads the private expected-outcome data. That is allowed here
because this is the evaluation harness, running as a separate Python process.
The AI platform never sees any of it.
"""

from __future__ import annotations

import time
from dataclasses import dataclass

from . import cluster, shell, workloads
from .config import Settings
from .scenarios import definitions, ground_truth


@dataclass
class ScenarioCheck:
    scenario: str
    produced_signal: bool
    reset_clean: bool
    detail: str

    @property
    def passed(self) -> bool:
        return self.produced_signal and self.reset_clean


def _restart_counts(settings: Settings) -> dict[str, int]:
    """Restart counts per pod, so later checks can measure the increase."""
    return {p["name"]: int(p["restarts"]) for p in workloads.pod_summary(settings)}


def _pod_reasons(settings: Settings, workload: str, baseline: dict[str, int] | None = None) -> set[str]:
    """Reasons currently visible on a workload's pods, e.g. OOMKilled."""
    reasons: set[str] = set()

    for pod in workloads.pod_summary(settings):
        if not pod["name"].startswith(workload):
            continue

        previous = (baseline or {}).get(pod["name"], 0)
        restarted_since_baseline = int(pod["restarts"]) > previous

        if pod["ready"] == "no":
            reasons.add("NotReady")

        if restarted_since_baseline:
            reasons.add("Restarted")

        # A waiting reason describes the pod NOW, so it always counts.
        if pod.get("waitingReason"):
            reasons.add(pod["waitingReason"])

        # A last-termination reason describes a container generation that may
        # long predate this scenario, so it only counts when the pod actually
        # restarted since the baseline.
        #
        # Without that distinction a stale reason - one pod still carried
        # "Unknown" from an unrelated Docker restart hours earlier - was read
        # as disruption caused by the scenario under test.
        if pod.get("lastTermination") and (restarted_since_baseline or baseline is None):
            reasons.add(pod["lastTermination"])

    return reasons


def _recent_logs(settings: Settings, workload: str, *, lines: int = 200) -> str:
    """Logs from the current AND previous container instance.

    The previous instance matters for any scenario that kills the process. A
    crashing container writes its last words, dies, and is replaced; plain
    `kubectl logs` shows the fresh instance, which has not said anything yet.
    Checking only the current instance made the crash and out-of-memory
    scenarios look as though they produced no log evidence at all, when in fact
    the evidence was one container generation back.
    """
    combined = []

    for extra in ([], ["--previous"]):
        result = cluster.kubectl(
            settings,
            "logs",
            "-n", settings.workload_namespace,
            "-l", f"app.kubernetes.io/name={workload}",
            f"--tail={lines}",
            "--all-containers=true",
            *extra,
            check=False,
        )
        if result.returncode == 0 and result.stdout:
            combined.append(result.stdout)

    return "\n".join(combined)


def _recent_events(settings: Settings, workload: str) -> str:
    result = cluster.kubectl(
        settings, "get", "events",
        "-n", settings.workload_namespace,
        "--sort-by=.lastTimestamp",
        check=False,
    )
    return "\n".join(line for line in (result.stdout or "").splitlines() if workload in line)


def check_scenario(settings: Settings, name: str) -> ScenarioCheck:
    """Run one scenario end to end and report whether it behaved as expected."""
    scenario = definitions.get(name)
    expected = ground_truth.expected_for(name)

    shell.step(f"Scenario check: {name}")

    definitions.apply(settings, scenario)

    # Setting an environment variable triggers a rolling update, so pods
    # restart as part of APPLYING the scenario. Those restarts are the
    # injection mechanism, not the fault, and counting them made the
    # payment-latency check report Kubernetes disruption in a scenario whose
    # defining feature is the absence of it.
    #
    # Waiting for the rollout to finish first means everything observed
    # afterwards is caused by the fault.
    if scenario.action == "set-env":
        cluster.wait_for_rollout(
            settings, settings.workload_namespace,
            f"deployment/{scenario.target_deployment}", timeout=300,
        )

    baseline = _restart_counts(settings)

    # Wait for the effect to appear. Kubernetes needs time to restart a
    # container or fail a probe, and the traffic generator needs time to send
    # enough requests for the symptom to be visible.
    delay = scenario.signal_delay_seconds
    shell.info(f"waiting {delay}s for the effect to become visible")
    time.sleep(delay)

    findings: list[str] = []
    produced = True

    # --- Kubernetes-visible evidence ---
    if expected.expected_pod_conditions:
        # Any pod of any affected workload showing one of the expected
        # conditions is enough; which replica is hit is not deterministic.
        observed: set[str] = set()
        for workload in expected.affected_workloads:
            observed |= _pod_reasons(settings, workload, baseline)

        # A readiness failure surfaces as an "Unhealthy" Kubernetes event and as
        # a NotReady pod. Both are the same finding seen from different angles,
        # so either satisfies the check.
        events = _recent_events(settings, expected.root_cause_workload)
        if "Unhealthy" in events:
            observed.add("Unhealthy")

        matched = [c for c in expected.expected_pod_conditions if c in observed]
        if matched:
            findings.append(f"pod condition {matched[0]}")
        else:
            produced = False
            findings.append(
                f"expected one of {expected.expected_pod_conditions} but saw {sorted(observed) or 'nothing'}"
            )

    # --- Log evidence ---
    if expected.expected_log_substrings:
        combined = "\n".join(
            _recent_logs(settings, workload) for workload in expected.affected_workloads
        ) + "\n" + _recent_logs(settings, expected.root_cause_workload)

        missing = [s for s in expected.expected_log_substrings if s.lower() not in combined.lower()]
        if missing:
            produced = False
            findings.append(f"log evidence missing: {missing}")
        else:
            findings.append("expected log evidence present")

    # --- The absence of Kubernetes disruption is itself evidence ---
    # For the payment-latency scenario, nothing in Kubernetes should look
    # wrong. If pods ARE restarting, the scenario is not reproducing the
    # dependency-latency situation it claims to.
    if not expected.expect_kubernetes_disruption:
        disruptions: set[str] = set()
        for workload in [*expected.affected_workloads, expected.root_cause_workload]:
            disruptions |= {r for r in _pod_reasons(settings, workload, baseline) if r != "NotReady"}

        if disruptions:
            produced = False
            findings.append(
                f"expected no Kubernetes disruption but saw {sorted(disruptions)}"
            )
        else:
            findings.append("no Kubernetes disruption, as expected")

    if produced:
        shell.ok(f"{name} produced its expected signals")
    else:
        shell.fail(f"{name} did not produce its expected signals")

    # --- Reset must restore health ---
    reset_clean = definitions.reset(settings, scenario)

    # The database scenario takes down services that depend on it, so those
    # have to recover too before the environment is genuinely clean.
    if reset_clean and name == "database-unavailable":
        for dependent in ("order-api", "notification-worker"):
            cluster.kubectl(
                settings, "rollout", "restart", f"deployment/{dependent}",
                "-n", settings.workload_namespace, check=False, capture=False,
            )
            if not cluster.wait_for_rollout(
                settings, settings.workload_namespace, f"deployment/{dependent}", timeout=300
            ):
                reset_clean = False

    return ScenarioCheck(
        scenario=name,
        produced_signal=produced,
        reset_clean=reset_clean,
        detail="; ".join(findings),
    )


def check_all(settings: Settings, *, only: list[str] | None = None) -> list[ScenarioCheck]:
    """Run every scenario in turn, always starting from a clean state."""
    targets = only or definitions.names()
    results: list[ScenarioCheck] = []

    shell.step("Establishing a clean baseline before scenario checks")
    definitions.reset_all(settings)

    for name in targets:
        results.append(check_scenario(settings, name))

    shell.step("Scenario check summary")
    for result in results:
        marker = "PASS" if result.passed else "FAIL"
        print(f"    [{marker}] {result.scenario:<22} {result.detail}")
        if not result.reset_clean:
            print(f"    {'':<29} reset did NOT restore a healthy workload")

    return results
