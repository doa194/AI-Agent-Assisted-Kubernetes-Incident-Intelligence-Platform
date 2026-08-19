"""Building, loading and deploying the demo workload.

There is no container registry in this project. Images are built locally and
pushed straight into the Kind nodes with `kind load docker-image`, and the
deployments use imagePullPolicy: Never so Kubernetes never tries to fetch them
from anywhere. That keeps the whole setup offline-capable after the first
bootstrap.
"""

from __future__ import annotations

from . import cluster, shell
from .config import K8S_DIR, REPO_ROOT, Settings

# Service name -> .NET project directory name.
# The service name is also the image name and the Kubernetes object name, so
# these three stay in step by construction.
SERVICES: dict[str, str] = {
    "gateway": "KubeSage.Workload.Gateway",
    "order-api": "KubeSage.Workload.OrderApi",
    "payment-simulator": "KubeSage.Workload.PaymentSimulator",
    "notification-worker": "KubeSage.Workload.NotificationWorker",
    "traffic-generator": "KubeSage.Workload.TrafficGenerator",
}

WORKLOAD_MANIFESTS = K8S_DIR / "workload"

# Images the cluster pulls from Docker Hub rather than building. They are
# preloaded so that a scenario reset, which recreates pods, does not depend on
# network access or hit a registry rate limit at an awkward moment.
EXTERNAL_IMAGES = ["postgres:18.2-trixie"]


def image_tag(service: str) -> str:
    return f"kubesage/{service}:local"


def build_images(settings: Settings, *, services: list[str] | None = None) -> None:
    """Build one container image per demo service."""
    targets = services or list(SERVICES)
    shell.step(f"Building {len(targets)} workload image(s)")

    for service in targets:
        project = SERVICES[service]
        shell.info(f"building {image_tag(service)}")
        shell.run(
            [
                "docker", "build",
                "-f", "src/workload/Dockerfile",
                "--build-arg", f"SERVICE={project}",
                "-t", image_tag(service),
                ".",
            ],
            cwd=str(REPO_ROOT),
            capture=False,
            timeout=2400,
        )

    shell.ok("workload images built")


def load_images(settings: Settings, *, services: list[str] | None = None) -> None:
    """Copy the built images into every Kind node."""
    targets = services or list(SERVICES)
    shell.step("Loading images into the Kind cluster")

    for service in targets:
        tag = image_tag(service)
        shell.info(f"loading {tag}")
        shell.run(
            ["kind", "load", "docker-image", tag, "--name", settings.cluster_name],
            capture=False,
            timeout=900,
        )

    # Third-party images are preloaded only as an optimisation, so that a
    # scenario reset does not have to reach the internet. Failure here is not
    # fatal: those deployments use the default IfNotPresent pull policy and
    # Kubernetes will fetch the image itself.
    #
    # This does fail in practice for multi-platform images pulled by Docker
    # Desktop, which cannot always be exported in the single-platform form
    # `kind load` expects.
    for image in EXTERNAL_IMAGES:
        try:
            shell.run(["docker", "pull", "--platform", "linux/amd64", image], capture=False, timeout=900)
            shell.run(
                ["kind", "load", "docker-image", image, "--name", settings.cluster_name],
                capture=False,
                timeout=900,
            )
            shell.info(f"preloaded {image}")
        except shell.CommandError:
            shell.warn(
                f"could not preload {image} into the cluster; "
                "Kubernetes will pull it from the registry instead"
            )

    shell.ok("workload images available on all nodes")


def deploy(settings: Settings) -> None:
    shell.step("Deploying the demo workload")
    cluster.apply_manifests(settings, str(WORKLOAD_MANIFESTS))
    shell.ok("workload manifests applied")


def wait_ready(settings: Settings, *, timeout: int = 420) -> bool:
    """Wait for the database first, then everything that depends on it."""
    shell.step("Waiting for the workload to become ready")

    namespace = settings.workload_namespace

    # The database has to be up before the services that use it can pass their
    # readiness probes, so it is waited for separately rather than in parallel.
    if not cluster.wait_for_rollout(settings, namespace, "deployment/workload-db", timeout=180):
        shell.fail("the workload database did not become ready")
        return False

    healthy = True
    for service in SERVICES:
        if not cluster.wait_for_rollout(settings, namespace, f"deployment/{service}", timeout=timeout):
            shell.fail(f"{service} did not become ready")
            healthy = False

    if healthy:
        shell.ok("all workload deployments are ready")

    return healthy


def pod_summary(settings: Settings) -> list[dict[str, str]]:
    """Compact per-pod status used by `status` and `verify`."""
    data = cluster.kubectl_json(settings, "get", "pods", "-n", settings.workload_namespace)
    pods = []

    for item in data.get("items", []):
        status = item.get("status", {})
        container_statuses = status.get("containerStatuses") or []
        restarts = sum(c.get("restartCount", 0) for c in container_statuses)
        ready = all(c.get("ready", False) for c in container_statuses) if container_statuses else False

        # Why a container is currently waiting (CrashLoopBackOff) AND why it
        # last terminated (OOMKilled, Error). Both, always - they answer
        # different questions and an earlier version reported only the first
        # one it found.
        #
        # That was wrong in the case where it mattered most: a container killed
        # for exceeding its memory limit immediately enters CrashLoopBackOff,
        # so returning the waiting reason and stopping discarded "OOMKilled"
        # entirely. An operator could then not tell an out-of-memory kill from
        # an ordinary crash, which is precisely the distinction that decides
        # whether the fix is a memory limit or a code change.
        waiting_reason = ""
        last_termination = ""

        for container in container_statuses:
            waiting = (container.get("state") or {}).get("waiting")
            if waiting and waiting.get("reason") and not waiting_reason:
                waiting_reason = waiting["reason"]

            last = (container.get("lastState") or {}).get("terminated")
            if last and last.get("reason") and not last_termination:
                last_termination = last["reason"]

        # Kept as one field for display; both parts are also exposed below so
        # callers can reason about them separately.
        reason = " ".join(
            part for part in (waiting_reason, f"last={last_termination}" if last_termination else "")
            if part
        )

        pods.append(
            {
                "name": item["metadata"]["name"],
                "phase": status.get("phase", "?"),
                "ready": "yes" if ready else "no",
                "restarts": str(restarts),
                "reason": reason,
                "waitingReason": waiting_reason,
                "lastTermination": last_termination,
                "node": item.get("spec", {}).get("nodeName", "?"),
            }
        )

    return sorted(pods, key=lambda p: p["name"])
