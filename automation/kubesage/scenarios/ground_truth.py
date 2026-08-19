"""Expected outcome of each failure scenario. PRIVATE EVALUATION DATA.

===========================================================================
This file is the answer key. It must never reach the AI platform or any
agent, directly or indirectly.
===========================================================================

Why that matters: the whole claim of this project is that the agents work out
what went wrong from real telemetry. If any of this text were visible to them
- in a prompt, in a log line, in a runbook, in the incident database - the
investigation results would prove nothing at all.

How the boundary is actually enforced, rather than merely intended:

  * This file lives in the Python automation package. The platform container
    image is built from src/ and knowledge/ only, so it is never copied in.
  * The automation talks to the platform over its public HTTP API. It only
    ever reads results; it never sends expectations.
  * Nothing here is written to Loki, Prometheus, the incident database or the
    runbook corpus.

It is used by two things only: the operational check that a scenario really
produced the telemetry it should, and the AI evaluation that scores a finished
report after the fact.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class ExpectedOutcome:
    """What a correct investigation should conclude."""

    scenario: str

    # Broad classification the triage step should reach.
    incident_category: str

    # Workloads a correct report must name as affected.
    affected_workloads: list[str]

    # Workload that is actually at fault. For a dependency failure this is
    # NOT the same as the workload showing the errors, which is exactly the
    # distinction the investigation has to get right.
    root_cause_workload: str

    # Short machine-comparable root cause label.
    root_cause_category: str

    # Signals that must appear in telemetry for the scenario to be considered
    # correctly reproduced. Checked by operational verification against Loki,
    # Prometheus and the Kubernetes API - never shown to an agent.
    expected_log_substrings: list[str] = field(default_factory=list)
    expected_pod_conditions: list[str] = field(default_factory=list)
    expect_kubernetes_disruption: bool = True

    # Claims that indicate the investigation reached the WRONG conclusion.
    # Used to catch a report that blames the visible symptom instead of the
    # underlying cause.
    incorrect_root_cause_workloads: list[str] = field(default_factory=list)


EXPECTED: dict[str, ExpectedOutcome] = {
    "app-crash": ExpectedOutcome(
        scenario="app-crash",
        incident_category="pod_restart_loop",
        affected_workloads=["order-api"],
        root_cause_workload="order-api",
        root_cause_category="application_process_failure",
        expected_log_substrings=["Simulated unrecoverable failure"],
        expected_pod_conditions=["CrashLoopBackOff", "Error"],
        expect_kubernetes_disruption=True,
        incorrect_root_cause_workloads=["payment-simulator", "workload-db"],
    ),
    "payment-latency": ExpectedOutcome(
        scenario="payment-latency",
        incident_category="dependency_latency",
        # The gateway and order API both show errors, but neither is broken.
        affected_workloads=["order-api", "gateway"],
        root_cause_workload="payment-simulator",
        root_cause_category="downstream_dependency_slow",
        expected_log_substrings=["timed out", "payment-simulator"],
        expected_pod_conditions=[],
        # The defining feature of this scenario: nothing in Kubernetes looks
        # wrong. A report that blames a pod problem has misread the evidence.
        expect_kubernetes_disruption=False,
        incorrect_root_cause_workloads=["order-api", "gateway", "workload-db"],
    ),
    "database-unavailable": ExpectedOutcome(
        scenario="database-unavailable",
        incident_category="dependency_unavailable",
        affected_workloads=["order-api", "notification-worker"],
        root_cause_workload="workload-db",
        root_cause_category="database_unavailable",
        expected_log_substrings=["workload-database", "unavailable"],
        expected_pod_conditions=[],
        expect_kubernetes_disruption=True,
        incorrect_root_cause_workloads=["payment-simulator", "gateway"],
    ),
    "readiness-failure": ExpectedOutcome(
        scenario="readiness-failure",
        incident_category="readiness_failure",
        affected_workloads=["notification-worker"],
        root_cause_workload="notification-worker",
        root_cause_category="readiness_probe_failing",
        expected_log_substrings=["Readiness probe is failing"],
        expected_pod_conditions=["Unhealthy"],
        expect_kubernetes_disruption=True,
        incorrect_root_cause_workloads=["workload-db", "order-api"],
    ),
    "oom-kill": ExpectedOutcome(
        scenario="oom-kill",
        incident_category="out_of_memory",
        affected_workloads=["payment-simulator"],
        root_cause_workload="payment-simulator",
        root_cause_category="container_memory_limit_exceeded",
        expected_log_substrings=["Allocating"],
        expected_pod_conditions=["OOMKilled"],
        expect_kubernetes_disruption=True,
        incorrect_root_cause_workloads=["order-api", "workload-db"],
    ),
}


def expected_for(scenario: str) -> ExpectedOutcome:
    if scenario not in EXPECTED:
        raise KeyError(f"No expected outcome recorded for scenario '{scenario}'.")
    return EXPECTED[scenario]
