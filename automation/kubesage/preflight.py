"""Checks the machine can actually run KubeSage before anything is created.

Bootstrapping downloads gigabytes and creates a three node cluster. Finding
out twenty minutes in that a port was taken or the disk was full is a poor
experience, so every prerequisite is checked up front and reported together.

The checks are deliberately advisory where they can be: a missing GPU or
slightly low memory produces a warning and a slower run, not a refusal.
"""

from __future__ import annotations

import socket
from dataclasses import dataclass

from . import shell
from .config import Settings

# Below this the platform will run but a 12B model plus a three node cluster
# will be uncomfortably tight, so the operator is told.
RECOMMENDED_DOCKER_MEMORY_GB = 12.0

# gemma4:12b alone is about 7.6 GB, plus container images and cluster state.
REQUIRED_FREE_DISK_GB = 25.0


@dataclass
class PreflightResult:
    passed: bool
    gpu_available: bool
    warnings: list[str]
    errors: list[str]


def _port_in_use(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.settimeout(1.0)
        return probe.connect_ex(("127.0.0.1", port)) == 0


def _docker_info() -> dict[str, str] | None:
    try:
        result = shell.run(
            ["docker", "info", "--format", "{{.NCPU}}|{{.MemTotal}}|{{json .Runtimes}}"]
        )
    except Exception:
        return None

    cpus, _, rest = result.stdout.strip().partition("|")
    memory, _, runtimes = rest.partition("|")
    return {"cpus": cpus, "memory": memory, "runtimes": runtimes}


def run_preflight(settings: Settings, *, expect_ports_free: bool) -> PreflightResult:
    """Verify prerequisites.

    expect_ports_free is False for commands that run against an environment
    that is supposed to already be up, where a listening port is a success
    rather than a conflict.
    """
    errors: list[str] = []
    warnings: list[str] = []
    gpu_available = False

    shell.step("Preflight checks")

    # --- Required tools ---
    for tool, hint in (
        ("docker", "Install Docker Desktop and make sure it is running."),
        ("kind", "Install kind: https://kind.sigs.k8s.io/docs/user/quick-start/"),
        ("kubectl", "Install kubectl to talk to the cluster."),
    ):
        if shell.which(tool) is None:
            errors.append(f"'{tool}' was not found on PATH. {hint}")
        else:
            shell.ok(f"{tool} found")

    if errors:
        # Without the tools nothing else can be checked meaningfully.
        return PreflightResult(False, False, warnings, errors)

    # --- Docker daemon ---
    info = _docker_info()
    if info is None:
        errors.append("The Docker daemon is not responding. Start Docker Desktop and retry.")
        return PreflightResult(False, False, warnings, errors)

    shell.ok("Docker daemon responding")

    # --- Docker compose v2 ---
    try:
        shell.run(["docker", "compose", "version"])
        shell.ok("docker compose available")
    except Exception:
        errors.append("'docker compose' (v2) is required but was not usable.")

    # --- Memory available to containers ---
    try:
        memory_gb = int(info["memory"]) / (1024**3)
        if memory_gb < RECOMMENDED_DOCKER_MEMORY_GB:
            warnings.append(
                f"Docker has {memory_gb:.1f} GB of memory. "
                f"{RECOMMENDED_DOCKER_MEMORY_GB:.0f} GB or more is recommended when running the "
                "12B reasoning model alongside a three node cluster. Expect slower analysis, "
                "and raise the memory limit in Docker Desktop settings if runs are unstable."
            )
        else:
            shell.ok(f"Docker memory {memory_gb:.1f} GB")
    except (KeyError, ValueError):
        warnings.append("Could not determine how much memory Docker has available.")

    # --- GPU ---
    # An NVIDIA GPU is optional. When present, part of the model runs on the
    # card and investigations finish noticeably faster.
    if "nvidia" in info.get("runtimes", ""):
        gpu_available = True
        shell.ok("NVIDIA container runtime detected - GPU acceleration will be enabled")
    else:
        warnings.append(
            "No NVIDIA container runtime detected. The model will run on the CPU, "
            "which works but makes each investigation considerably slower."
        )

    # --- Disk ---
    try:
        result = shell.run(["docker", "system", "df", "--format", "{{.Type}}|{{.Size}}"])
        shell.info(f"docker disk usage:\n        " + result.stdout.strip().replace("\n", "\n        "))
    except Exception:
        pass

    # --- Host ports ---
    port_keys = [
        ("KIND_API_HOST_PORT", "Kubernetes API"),
        ("LOKI_HOST_PORT", "Loki"),
        ("PROMETHEUS_HOST_PORT", "Prometheus"),
        ("GATEWAY_HOST_PORT", "demo gateway"),
        ("OLLAMA_HOST_PORT", "Ollama"),
        ("POSTGRES_HOST_PORT", "PostgreSQL"),
        ("GRAFANA_HOST_PORT", "Grafana"),
        ("PLATFORM_HOST_PORT", "KubeSage API"),
    ]

    if expect_ports_free:
        busy = [
            f"{port} ({label})"
            for key, label in port_keys
            if _port_in_use(port := settings.port(key))
        ]
        if busy:
            warnings.append(
                "These host ports are already in use: "
                + ", ".join(busy)
                + ". If that is a previous KubeSage run, 'cleanup' will free them. "
                "Otherwise change the port in versions.env."
            )
        else:
            shell.ok("all required host ports are free")

    for warning in warnings:
        shell.warn(warning)
    for error in errors:
        shell.fail(error)

    return PreflightResult(
        passed=not errors,
        gpu_available=gpu_available,
        warnings=warnings,
        errors=errors,
    )
