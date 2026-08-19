"""Repository layout and pinned versions, resolved once for every command.

Why this file exists: every other automation module needs to know where the
repository is and which image versions to use. Working that out in one place
means a command can never accidentally use a different Loki version than the
one the manifests were rendered with.

Nothing here reaches out to Docker or Kubernetes. It is pure path and value
resolution, so it stays fast and easy to reason about.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

# automation/kubesage/config.py -> repository root is three levels up.
REPO_ROOT = Path(__file__).resolve().parents[2]

VERSIONS_FILE = REPO_ROOT / "versions.env"
COMPOSE_DIR = REPO_ROOT / "deploy" / "compose"
COMPOSE_FILE = COMPOSE_DIR / "docker-compose.yml"
COMPOSE_GPU_FILE = COMPOSE_DIR / "docker-compose.gpu.yml"
COMPOSE_GENERATED_DIR = COMPOSE_DIR / "generated"
KIND_DIR = REPO_ROOT / "deploy" / "kind"
KIND_CLUSTER_TEMPLATE = KIND_DIR / "cluster.yaml"
K8S_DIR = REPO_ROOT / "deploy" / "k8s"
SRC_DIR = REPO_ROOT / "src"

# Scratch space for rendered manifests and generated kubeconfig files.
BUILD_DIR = REPO_ROOT / ".kubesage-build"


def load_versions() -> dict[str, str]:
    """Read versions.env into a plain dictionary.

    A tiny hand-rolled parser is used rather than a dotenv library so the
    automation keeps working with a bare Python install and no pip step.
    """
    values: dict[str, str] = {}

    if not VERSIONS_FILE.exists():
        raise FileNotFoundError(f"Missing pinned version file: {VERSIONS_FILE}")

    for raw_line in VERSIONS_FILE.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip()

    return values


@dataclass(frozen=True)
class Settings:
    """Everything a command needs to know about this environment."""

    versions: dict[str, str]

    # --- Cluster ---
    @property
    def cluster_name(self) -> str:
        return self.versions["KIND_CLUSTER_NAME"]

    @property
    def kind_node_image(self) -> str:
        return self.versions["KIND_NODE_IMAGE"]

    @property
    def kind_context(self) -> str:
        # kind always prefixes the context name it writes to kubeconfig.
        return f"kind-{self.cluster_name}"

    # --- Models ---
    @property
    def chat_model(self) -> str:
        return self.versions["KUBESAGE_CHAT_MODEL"]

    @property
    def embedding_model(self) -> str:
        return self.versions["KUBESAGE_EMBEDDING_MODEL"]

    # --- Host ports ---
    def port(self, key: str) -> int:
        return int(self.versions[key])

    @property
    def ollama_url(self) -> str:
        return f"http://127.0.0.1:{self.port('OLLAMA_HOST_PORT')}"

    @property
    def loki_url(self) -> str:
        return f"http://127.0.0.1:{self.port('LOKI_HOST_PORT')}"

    @property
    def prometheus_url(self) -> str:
        return f"http://127.0.0.1:{self.port('PROMETHEUS_HOST_PORT')}"

    @property
    def gateway_url(self) -> str:
        return f"http://127.0.0.1:{self.port('GATEWAY_HOST_PORT')}"

    @property
    def grafana_url(self) -> str:
        return f"http://127.0.0.1:{self.port('GRAFANA_HOST_PORT')}"

    @property
    def platform_url(self) -> str:
        return f"http://127.0.0.1:{self.port('PLATFORM_HOST_PORT')}"

    @property
    def kubernetes_api_url(self) -> str:
        return f"https://127.0.0.1:{self.port('KIND_API_HOST_PORT')}"

    # --- Namespaces ---
    @property
    def workload_namespace(self) -> str:
        return "kubesage-demo"

    @property
    def observability_namespace(self) -> str:
        return "kubesage-observability"

    def image(self, key: str) -> str:
        return self.versions[key]


def load_settings() -> Settings:
    return Settings(versions=load_versions())


def compose_env() -> dict[str, str]:
    """Environment for `docker compose`, combining the shell and versions.env.

    Compose is also given --env-file, but variables are put in the process
    environment as well so that shelled-out helpers see the same values.
    """
    env = dict(os.environ)
    env.update(load_versions())
    return env
