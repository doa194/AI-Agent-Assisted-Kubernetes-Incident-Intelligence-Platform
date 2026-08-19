"""The five controlled failure scenarios.

Every scenario is applied by changing the Kubernetes deployment - setting an
environment variable or changing the replica count - and reset by undoing
exactly that change. Nothing reaches into a running container, and no service
exposes an endpoint that can break it.

That design has three benefits worth stating:

  * the change appears in the cluster as a real deployment event, so the
    evidence an investigator sees is genuine Kubernetes activity;
  * reset is precise, because it is the inverse of a single declarative edit;
  * no unauthenticated "break yourself" route exists in the demo services.

The expected OUTCOME of each scenario lives in ground_truth.py, not here. This
module only knows how to cause and undo a failure.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from .. import cluster, shell
from ..config import Settings

FaultAction = Literal["set-env", "scale"]


@dataclass(frozen=True)
class Scenario:
    """How to cause and undo one failure."""

    name: str
    # Shown to an operator running the scenario. Deliberately describes the
    # ACTION taken, not the conclusion an investigator should reach.
    summary: str
    target_deployment: str
    action: FaultAction

    # For "set-env": the variable and the value that activates the fault.
    env_name: str = ""
    env_value: str = ""

    # For "scale": the replica count while the scenario is active, and the
    # count to restore.
    broken_replicas: int = 0
    healthy_replicas: int = 1

    # How long to wait after applying before the signal is expected to be
    # visible in telemetry. Used by the verification command.
    signal_delay_seconds: int = 90


SCENARIOS: dict[str, Scenario] = {
    "app-crash": Scenario(
        name="app-crash",
        summary=(
            "The order API process exits with a failure code shortly after each start, "
            "so Kubernetes restarts it repeatedly and it enters CrashLoopBackOff."
        ),
        target_deployment="order-api",
        action="set-env",
        env_name="KUBESAGE_FAULT_CRASH_AFTER_SECONDS",
        env_value="25",
        signal_delay_seconds=120,
    ),
    "payment-latency": Scenario(
        name="payment-latency",
        summary=(
            "The payment simulator adds three seconds to every response, which is "
            "past the order API's two second timeout. No Kubernetes object becomes "
            "unhealthy - only the application's own error rate moves."
        ),
        target_deployment="payment-simulator",
        action="set-env",
        env_name="KUBESAGE_FAULT_LATENCY_MS",
        env_value="3000",
        signal_delay_seconds=90,
    ),
    "database-unavailable": Scenario(
        name="database-unavailable",
        summary=(
            "The workload database is scaled to zero replicas, so both the order API "
            "and the notification worker lose their storage dependency at once."
        ),
        target_deployment="workload-db",
        action="scale",
        broken_replicas=0,
        healthy_replicas=1,
        signal_delay_seconds=90,
    ),
    "readiness-failure": Scenario(
        name="readiness-failure",
        summary=(
            "The notification worker's readiness probe starts failing while the "
            "process keeps running, so Kubernetes removes it from service endpoints "
            "without restarting it."
        ),
        target_deployment="notification-worker",
        action="set-env",
        env_name="KUBESAGE_FAULT_UNREADY",
        env_value="true",
        signal_delay_seconds=75,
    ),
    "oom-kill": Scenario(
        name="oom-kill",
        summary=(
            "The payment simulator allocates far more memory than its container limit "
            "allows, so the kernel terminates it and Kubernetes reports OOMKilled."
        ),
        target_deployment="payment-simulator",
        action="set-env",
        env_name="KUBESAGE_FAULT_ALLOCATE_MB",
        env_value="512",
        signal_delay_seconds=120,
    ),
}


def names() -> list[str]:
    return list(SCENARIOS)


def get(name: str) -> Scenario:
    if name not in SCENARIOS:
        raise KeyError(
            f"Unknown scenario '{name}'. Available: {', '.join(sorted(SCENARIOS))}"
        )
    return SCENARIOS[name]


def apply(settings: Settings, scenario: Scenario) -> None:
    """Activate the fault."""
    shell.step(f"Starting scenario '{scenario.name}'")
    shell.info(scenario.summary)

    namespace = settings.workload_namespace

    if scenario.action == "set-env":
        cluster.kubectl(
            settings,
            "set", "env",
            f"deployment/{scenario.target_deployment}",
            f"{scenario.env_name}={scenario.env_value}",
            "-n", namespace,
            capture=False,
        )
    else:
        cluster.kubectl(
            settings,
            "scale", f"deployment/{scenario.target_deployment}",
            f"--replicas={scenario.broken_replicas}",
            "-n", namespace,
            capture=False,
        )

    shell.ok(f"scenario '{scenario.name}' applied to {scenario.target_deployment}")
    shell.info(
        f"expect telemetry to show the effect within about {scenario.signal_delay_seconds}s"
    )


def reset(settings: Settings, scenario: Scenario, *, wait: bool = True) -> bool:
    """Undo the fault and wait for the workload to be healthy again."""
    shell.step(f"Resetting scenario '{scenario.name}'")

    namespace = settings.workload_namespace

    if scenario.action == "set-env":
        # A trailing dash removes the variable, which is the exact inverse of
        # how it was set.
        cluster.kubectl(
            settings,
            "set", "env",
            f"deployment/{scenario.target_deployment}",
            f"{scenario.env_name}-",
            "-n", namespace,
            capture=False,
        )
    else:
        cluster.kubectl(
            settings,
            "scale", f"deployment/{scenario.target_deployment}",
            f"--replicas={scenario.healthy_replicas}",
            "-n", namespace,
            capture=False,
        )

    if not wait:
        return True

    healthy = cluster.wait_for_rollout(
        settings, namespace, f"deployment/{scenario.target_deployment}", timeout=300
    )

    if healthy:
        shell.ok(f"{scenario.target_deployment} is healthy again")
    else:
        shell.fail(f"{scenario.target_deployment} did not return to a healthy state")

    return healthy


def reset_all(settings: Settings) -> bool:
    """Clear every fault, whether or not it is currently active.

    Used to guarantee a known-good starting point before a scenario or an
    end-to-end test, so a leftover fault from an interrupted run cannot be
    mistaken for a new incident.
    """
    shell.step("Clearing all injected faults")
    healthy = True

    for scenario in SCENARIOS.values():
        try:
            reset(settings, scenario, wait=False)
        except shell.CommandError as exc:
            shell.warn(f"could not reset {scenario.name}: {exc}")
            healthy = False

    for deployment in {s.target_deployment for s in SCENARIOS.values()}:
        if not cluster.wait_for_rollout(
            settings, settings.workload_namespace, f"deployment/{deployment}", timeout=300
        ):
            healthy = False

    if healthy:
        shell.ok("all faults cleared and the workload is healthy")

    return healthy
