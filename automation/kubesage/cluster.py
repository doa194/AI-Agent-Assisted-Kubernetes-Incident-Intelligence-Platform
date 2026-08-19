"""Creating, inspecting and deleting the three node Kind cluster.

The cluster definition lives in deploy/kind/cluster.yaml with a placeholder
for the node image. That placeholder is filled in here from versions.env, so
the pinned Kubernetes version is stated in exactly one place even though kind
needs it inside its own config file.
"""

from __future__ import annotations

import json
import time

from . import shell
from .config import BUILD_DIR, KIND_CLUSTER_TEMPLATE, Settings

NODE_IMAGE_PLACEHOLDER = "KIND_NODE_IMAGE_PLACEHOLDER"


def kubectl(settings: Settings, *args: str, check: bool = True, capture: bool = True, timeout: int | None = None):
    """Run kubectl against the KubeSage cluster.

    The context is always passed explicitly. Relying on whatever context
    happens to be current is how automation ends up modifying the wrong
    cluster, which is unacceptable for a tool that also runs failure
    scenarios.
    """
    return shell.run(
        ["kubectl", "--context", settings.kind_context, *args],
        check=check,
        capture=capture,
        timeout=timeout,
    )


def kubectl_json(settings: Settings, *args: str, attempts: int = 3):
    """Run kubectl and parse its JSON, retrying transient API failures.

    The API server on a single-machine setup occasionally refuses a connection
    or times out its TLS handshake when the host is saturated - a three node
    cluster, the full demo workload, the observability stack and a 12B model all
    compete for the same CPU. That is a momentary condition, not a fault, and
    aborting a fifteen minute verification run because of one is unhelpful.

    Only genuinely transient symptoms are retried. A malformed request or a
    permission error fails immediately, because retrying those just hides them.
    """
    transient = (
        "TLS handshake timeout",
        "connection refused",
        "i/o timeout",
        "unexpected EOF",
        "etcdserver: request timed out",
    )

    last_error: Exception | None = None

    for attempt in range(1, attempts + 1):
        try:
            result = kubectl(settings, *args, "-o", "json")
            return json.loads(result.stdout)
        except shell.CommandError as exc:
            if not any(symptom in exc.output for symptom in transient) or attempt == attempts:
                raise

            last_error = exc
            shell.warn(
                f"kubectl call failed transiently (attempt {attempt}/{attempts}); "
                "the API server is probably busy, retrying"
            )
            time.sleep(3 * attempt)
        except json.JSONDecodeError as exc:
            if attempt == attempts:
                raise
            last_error = exc
            time.sleep(3 * attempt)

    raise last_error if last_error else RuntimeError("kubectl_json exhausted its attempts")


def exists(settings: Settings) -> bool:
    try:
        result = shell.run(["kind", "get", "clusters"])
    except Exception:
        return False
    return settings.cluster_name in result.stdout.split()


def render_config(settings: Settings) -> str:
    """Write the kind config with the pinned node image substituted in."""
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    rendered = KIND_CLUSTER_TEMPLATE.read_text(encoding="utf-8").replace(
        NODE_IMAGE_PLACEHOLDER, settings.kind_node_image
    )
    target = BUILD_DIR / "kind-cluster.rendered.yaml"
    target.write_text(rendered, encoding="utf-8")
    return str(target)


def create(settings: Settings) -> None:
    if exists(settings):
        shell.ok(f"Kind cluster '{settings.cluster_name}' already exists")
        return

    shell.step(f"Creating three node Kind cluster '{settings.cluster_name}'")
    config_path = render_config(settings)

    # Not captured: creating a cluster takes minutes and the operator should
    # see kind's own progress output rather than a silent prompt.
    shell.run(
        ["kind", "create", "cluster", "--config", config_path, "--wait", "180s"],
        capture=False,
        timeout=900,
    )
    shell.ok("cluster created")


def delete(settings: Settings) -> None:
    if not exists(settings):
        shell.info(f"Kind cluster '{settings.cluster_name}' does not exist")
        return

    shell.step(f"Deleting Kind cluster '{settings.cluster_name}'")
    shell.run(["kind", "delete", "cluster", "--name", settings.cluster_name], capture=False)
    shell.ok("cluster deleted")


def node_status(settings: Settings) -> list[tuple[str, str, str]]:
    """Return (name, ready condition, role) for every node."""
    data = kubectl_json(settings, "get", "nodes")
    nodes: list[tuple[str, str, str]] = []

    for item in data.get("items", []):
        name = item["metadata"]["name"]
        labels = item["metadata"].get("labels", {})
        role = "control-plane" if "node-role.kubernetes.io/control-plane" in labels else "worker"
        ready = "Unknown"
        for condition in item.get("status", {}).get("conditions", []):
            if condition.get("type") == "Ready":
                ready = condition.get("status", "Unknown")
        nodes.append((name, ready, role))

    return nodes


def all_nodes_ready(settings: Settings, *, expected: int = 3) -> bool:
    try:
        nodes = node_status(settings)
    except Exception:
        return False
    return len(nodes) >= expected and all(ready == "True" for _, ready, _ in nodes)


def wait_ready(settings: Settings, *, timeout: int = 300) -> bool:
    return shell.wait_until(
        "all cluster nodes Ready",
        lambda: all_nodes_ready(settings),
        timeout=timeout,
        interval=5,
    )


def apply_manifests(settings: Settings, path: str, *, prune_label: str | None = None) -> None:
    """Apply a manifest file or directory to the cluster."""
    args = ["apply", "-f", path]
    if prune_label:
        args += ["--prune", "-l", prune_label]
    kubectl(settings, *args, capture=False)


def namespace_exists(settings: Settings, namespace: str) -> bool:
    result = kubectl(settings, "get", "namespace", namespace, check=False)
    return result.returncode == 0


def wait_for_rollout(settings: Settings, namespace: str, kind_and_name: str, *, timeout: int = 300) -> bool:
    """Wait for a Deployment/DaemonSet/StatefulSet to finish rolling out."""
    result = kubectl(
        settings,
        "rollout",
        "status",
        kind_and_name,
        "-n",
        namespace,
        f"--timeout={timeout}s",
        check=False,
        capture=False,
        timeout=timeout + 60,
    )
    return result.returncode == 0
