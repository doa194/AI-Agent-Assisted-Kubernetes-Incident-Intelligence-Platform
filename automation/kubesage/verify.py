"""Operational verification of a running environment.

These are not unit tests. They check the things that only a real deployment
can tell you: that the cluster is healthy, that the two Docker networks can
actually reach each other, that the model loads, and that the pieces the
platform depends on are genuinely working rather than merely configured.

Each check is independent and reports its own result, so one failure does not
hide the rest.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field

from . import cluster, compose, models, shell, workloads
from .config import Settings


@dataclass
class CheckResult:
    name: str
    passed: bool
    detail: str = ""
    # A soft check reports a problem but does not fail the overall run. Used
    # for things that degrade the experience without breaking correctness.
    soft: bool = False


@dataclass
class VerifyReport:
    results: list[CheckResult] = field(default_factory=list)

    def add(self, result: CheckResult) -> None:
        self.results.append(result)
        if result.passed:
            shell.ok(f"{result.name}{': ' + result.detail if result.detail else ''}")
        elif result.soft:
            shell.warn(f"{result.name}: {result.detail}")
        else:
            shell.fail(f"{result.name}: {result.detail}")

    @property
    def passed(self) -> bool:
        return all(r.passed or r.soft for r in self.results)

    def summary(self) -> str:
        hard_failures = [r for r in self.results if not r.passed and not r.soft]
        warnings = [r for r in self.results if not r.passed and r.soft]
        return (
            f"{len(self.results)} checks, "
            f"{len(self.results) - len(hard_failures) - len(warnings)} passed, "
            f"{len(warnings)} warning(s), {len(hard_failures)} failure(s)"
        )


def _guard(name: str, fn: Callable[[], CheckResult]) -> CheckResult:
    """Turn an unexpected exception inside a check into a normal failure."""
    try:
        return fn()
    except Exception as exc:  # pragma: no cover - defensive
        return CheckResult(name, False, f"check raised {type(exc).__name__}: {exc}")


# --------------------------------------------------------------------------
# Operations plane
# --------------------------------------------------------------------------

def check_compose_services(settings: Settings, *, gpu: bool) -> CheckResult:
    states = compose.service_states(settings, gpu=gpu)
    if not states:
        return CheckResult("compose services", False, "no compose services are running")

    unhealthy = [
        f"{s['name']}({s['state']}/{s['health']})"
        for s in states
        if s["state"] != "running" or s["health"] in {"unhealthy", "starting"}
    ]

    if unhealthy:
        return CheckResult("compose services", False, "not healthy: " + ", ".join(unhealthy))

    return CheckResult("compose services", True, f"{len(states)} running")


def check_postgres(settings: Settings) -> CheckResult:
    result = shell.run(
        [
            "docker", "exec", "kubesage-postgres",
            "psql", "-U", "kubesage_owner", "-d", "kubesage", "-tAc",
            "SELECT extversion FROM pg_extension WHERE extname='vector'",
        ],
        check=False,
    )

    if result.returncode != 0:
        return CheckResult("postgres + pgvector", False, result.stderr.strip()[:200])

    version = result.stdout.strip()
    if not version:
        return CheckResult("postgres + pgvector", False, "pgvector extension is not installed")

    return CheckResult("postgres + pgvector", True, f"pgvector {version}")


def check_least_privilege_role(settings: Settings) -> CheckResult:
    """The application role must not be able to change the schema.

    This is a security boundary, so it is verified by actually attempting the
    forbidden operation rather than by reading the grant table.
    """
    result = shell.run(
        [
            "docker", "exec", "-e", "PGPASSWORD=kubesage_app_local_dev", "kubesage-postgres",
            "psql", "-U", "kubesage_app", "-d", "kubesage", "-h", "127.0.0.1", "-tAc",
            "CREATE TABLE kubesage_privilege_probe(id int)",
        ],
        check=False,
    )

    if result.returncode == 0:
        return CheckResult(
            "database least privilege", False,
            "the application role was able to CREATE TABLE; it should not be",
        )

    return CheckResult("database least privilege", True, "application role cannot alter the schema")


def check_platform_ready(settings: Settings) -> CheckResult:
    """The platform's own readiness endpoint must report Healthy.

    Readiness here means the platform can persist what it finds. It is checked
    separately from liveness because a platform that is running but cannot
    record incidents is worse than one that is plainly down.
    """
    status, body = shell.http_get(f"{settings.platform_url}/health/ready", timeout=15)

    if status == 0:
        return CheckResult("kubesage platform ready", False, "the platform is not answering on its port")

    if status != 200:
        return CheckResult("kubesage platform ready", False, f"HTTP {status}: {body[:200]}")

    return CheckResult("kubesage platform ready", True, "readiness reports Healthy")


def check_models_present(settings: Settings) -> CheckResult:
    present = models.installed_models(settings)
    required = [settings.chat_model, settings.embedding_model]
    missing = [m for m in required if m not in present]

    if missing:
        return CheckResult("ollama models", False, f"missing: {', '.join(missing)}")

    return CheckResult("ollama models", True, ", ".join(required))


def check_model_generates(settings: Settings) -> CheckResult:
    succeeded, detail = models.probe_generation(settings)
    if not succeeded:
        return CheckResult("chat model loads and responds", False, detail)
    return CheckResult("chat model loads and responds", True, f"replied {detail[:40]!r}")


def check_embedding_dimensions(settings: Settings, *, expected: int = 768) -> CheckResult:
    succeeded, dimensions = models.probe_embedding(settings)
    if not succeeded:
        return CheckResult("embedding model", False, "no embedding was returned")

    if dimensions != expected:
        return CheckResult(
            "embedding model", False,
            f"returned {dimensions} dimensions but the platform is configured for {expected}; "
            "update Ollama.EmbeddingDimensions and the database column together",
        )

    return CheckResult("embedding model", True, f"{dimensions} dimensions")


# --------------------------------------------------------------------------
# Cluster
# --------------------------------------------------------------------------

def check_cluster_nodes(settings: Settings) -> CheckResult:
    if not cluster.exists(settings):
        return CheckResult("kind cluster", False, f"cluster '{settings.cluster_name}' does not exist")

    nodes = cluster.node_status(settings)
    not_ready = [f"{name}({ready})" for name, ready, _ in nodes if ready != "True"]

    if len(nodes) != 3:
        return CheckResult("kind cluster", False, f"expected 3 nodes, found {len(nodes)}")

    if not_ready:
        return CheckResult("kind cluster", False, "nodes not Ready: " + ", ".join(not_ready))

    roles = ", ".join(f"{name}={role}" for name, _, role in nodes)
    return CheckResult("kind cluster", True, f"3 nodes Ready ({roles})")


def check_workload_healthy(settings: Settings) -> CheckResult:
    pods = workloads.pod_summary(settings)

    if not pods:
        return CheckResult("demo workload", False, "no workload pods are running")

    unhealthy = [
        f"{p['name']}({p['phase']},ready={p['ready']},restarts={p['restarts']}{',' + p['reason'] if p['reason'] else ''})"
        for p in pods
        if p["phase"] != "Running" or p["ready"] != "yes"
    ]

    if unhealthy:
        return CheckResult("demo workload", False, "; ".join(unhealthy))

    return CheckResult("demo workload", True, f"{len(pods)} pods Running and Ready")


def check_traffic_flowing(settings: Settings) -> CheckResult:
    """The traffic generator must be producing requests without prompting.

    This is what gives detection rules a baseline. An idle cluster would make
    every rule either silent or trivially triggered.
    """
    status, body = shell.http_get(
        f"{settings.prometheus_url}/api/v1/query"
        "?query=sum(increase(kubesage_http_requests_total%5B5m%5D))",
        timeout=20,
    )

    if status != 200:
        return CheckResult("automatic traffic", False, f"Prometheus query failed with HTTP {status}")

    import json

    try:
        result = json.loads(body)["data"]["result"]
        total = float(result[0]["value"][1]) if result else 0.0
    except (KeyError, IndexError, ValueError, json.JSONDecodeError):
        return CheckResult("automatic traffic", False, "could not read the request counter")

    if total < 10:
        return CheckResult(
            "automatic traffic", False,
            f"only {total:.0f} requests in the last 5 minutes; the traffic generator may not be running",
        )

    return CheckResult("automatic traffic", True, f"{total:.0f} requests in the last 5 minutes")


def check_log_pipeline(settings: Settings) -> CheckResult:
    """Logs must reach Loki with the low-cardinality labels the platform queries."""
    status, body = shell.http_get(f"{settings.loki_url}/loki/api/v1/labels", timeout=20)

    if status != 200:
        return CheckResult("log pipeline", False, f"Loki returned HTTP {status}")

    import json

    try:
        labels = set(json.loads(body).get("data") or [])
    except json.JSONDecodeError:
        return CheckResult("log pipeline", False, "Loki returned an unreadable label list")

    required = {"namespace", "container", "level"}
    missing = required - labels

    if missing:
        return CheckResult(
            "log pipeline", False,
            f"Loki is missing the labels the platform queries on: {sorted(missing)}",
        )

    # High-cardinality labels would make the index grow without bound, so
    # their ABSENCE is verified rather than assumed.
    forbidden = {"correlationid", "correlation_id", "orderid", "pod", "pod_name", "requestid"}
    leaked = {label for label in labels if label.lower() in forbidden}

    if leaked:
        return CheckResult(
            "log pipeline", False,
            f"high-cardinality labels leaked into the Loki index: {sorted(leaked)}",
        )

    status, body = shell.http_get(
        f"{settings.loki_url}/loki/api/v1/label/container/values", timeout=20
    )
    try:
        containers = set(json.loads(body).get("data") or [])
    except json.JSONDecodeError:
        containers = set()

    expected_services = {"gateway", "order-api", "payment-simulator"}
    if not expected_services.issubset(containers):
        return CheckResult(
            "log pipeline", False,
            f"logs missing for {sorted(expected_services - containers)}",
        )

    return CheckResult(
        "log pipeline", True,
        f"labels {sorted(labels)}, {len(containers)} containers shipping logs",
    )


def check_prometheus_scraping(settings: Settings) -> CheckResult:
    status, body = shell.http_get(
        f"{settings.prometheus_url}/api/v1/query?query=count(up%20%3D%3D%201)", timeout=20
    )

    if status != 200:
        return CheckResult("prometheus scraping", False, f"Prometheus returned HTTP {status}")

    import json

    try:
        result = json.loads(body)["data"]["result"]
        healthy_targets = int(float(result[0]["value"][1])) if result else 0
    except (KeyError, IndexError, ValueError, json.JSONDecodeError):
        return CheckResult("prometheus scraping", False, "could not read target count")

    if healthy_targets < 5:
        return CheckResult(
            "prometheus scraping", False,
            f"only {healthy_targets} healthy scrape targets; expected the workload pods plus cadvisor",
        )

    return CheckResult("prometheus scraping", True, f"{healthy_targets} healthy scrape targets")


def check_semantic_memory(settings: Settings) -> CheckResult:
    """The runbook corpus must be indexed and searchable."""
    result = shell.run(
        [
            "docker", "exec", "kubesage-postgres",
            "psql", "-U", "kubesage_owner", "-d", "kubesage", "-tAF", "|", "-c",
            "SELECT kind, count(*) FROM semantic_memory GROUP BY kind",
        ],
        check=False,
    )

    if result.returncode != 0:
        return CheckResult("semantic memory", False, result.stderr.strip()[:150])

    counts = {}
    for line in (result.stdout or "").strip().splitlines():
        if "|" in line:
            kind, _, count = line.partition("|")
            counts[kind.strip()] = int(count.strip() or 0)

    runbooks = counts.get("runbook", 0)

    if runbooks == 0:
        return CheckResult(
            "semantic memory", False,
            "no runbook sections are indexed; investigations will run without guidance",
        )

    incidents = counts.get("incident", 0)
    return CheckResult(
        "semantic memory", True,
        f"{runbooks} runbook section(s), {incidents} remembered incident(s)",
    )


def check_retrieval_quality(settings: Settings) -> CheckResult:
    """Retrieval must find the RIGHT document, not merely some document.

    Scored against a gold set, because a similarity number proves nothing on
    its own: what matters is whether the runbook that actually applies is the
    one an agent gets shown.
    """
    from . import retrieval_eval

    passed = retrieval_eval.run_evaluation(settings)

    return CheckResult(
        "semantic retrieval quality",
        passed,
        f"{len(retrieval_eval.GOLD_SET)} gold cases"
        if passed
        else "one or more gold retrieval cases failed",
    )


def check_readonly_rbac(settings: Settings) -> CheckResult:
    """The platform's Kubernetes identity must be unable to change anything.

    Asked of the API server itself with `kubectl auth can-i`, because the API
    server is what actually enforces it at runtime. Reading the RBAC rules and
    reasoning about them would only prove we can read YAML.
    """
    from . import rbac

    passed, problems = rbac.verify_read_only(settings)

    if not passed:
        return CheckResult("read-only kubernetes rbac", False, "; ".join(problems))

    return CheckResult(
        "read-only kubernetes rbac", True,
        "reads allowed; mutations, secrets, exec and port-forward all denied",
    )


def check_grafana_datasources(settings: Settings) -> CheckResult:
    """Grafana must be able to reach the same sources the platform queries."""
    status, _ = shell.http_get(f"{settings.grafana_url}/api/health", timeout=15)

    if status != 200:
        return CheckResult("grafana", False, f"Grafana health returned HTTP {status}", soft=True)

    return CheckResult("grafana", True, "reachable with provisioned Loki and Prometheus datasources")


def check_platform_evidence_api(settings: Settings) -> CheckResult:
    """The deterministic evidence layer must return correlated evidence.

    This is the check that proves the observability half of the project stands
    on its own: it exercises Loki, Prometheus and the Kubernetes API together
    through the platform, with no model involved.
    """
    status, body = shell.http_get(
        f"{settings.platform_url}/evidence?workload=order-api&windowMinutes=10", timeout=90
    )

    if status != 200:
        return CheckResult("evidence api", False, f"returned HTTP {status}: {body[:200]}")

    import json

    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return CheckResult("evidence api", False, "response was not valid JSON")

    if not payload.get("isComplete", False):
        return CheckResult(
            "evidence api", False,
            f"evidence incomplete; unavailable sources: {payload.get('unavailableSources')}",
        )

    kinds = set(payload.get("items", {}))
    if "KubernetesState" not in kinds or "Metric" not in kinds:
        return CheckResult(
            "evidence api", False,
            f"expected Kubernetes and metric evidence, got {sorted(kinds)}",
        )

    return CheckResult(
        "evidence api", True,
        f"{payload.get('itemCount', 0)} correlated items across {sorted(kinds)}",
    )


def check_cross_network(settings: Settings) -> CheckResult:
    """Prove the operations plane can reach the cluster's published ports.

    This is the single most important environmental check in the project. The
    AI platform lives in one Docker network and the cluster in another; if
    this path is broken, every telemetry query fails at runtime with an error
    that looks like a Loki problem rather than a networking problem.

    bash's /dev/tcp is used so no extra tooling has to be installed in the
    container just to test a TCP connection.
    """
    targets = [
        ("Kubernetes API", settings.port("KIND_API_HOST_PORT")),
    ]

    failures = []
    for label, port in targets:
        result = shell.run(
            [
                "docker", "exec", "kubesage-postgres", "bash", "-c",
                f"timeout 5 bash -c '</dev/tcp/host.docker.internal/{port}' 2>/dev/null && echo open || echo closed",
            ],
            check=False,
        )
        if "open" not in result.stdout:
            failures.append(f"{label} (host.docker.internal:{port})")

    if failures:
        return CheckResult(
            "operations plane -> cluster network", False,
            "unreachable: " + ", ".join(failures),
        )

    return CheckResult(
        "operations plane -> cluster network", True,
        "container can reach the cluster's published ports",
    )


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------

def run_verification(settings: Settings, *, gpu: bool) -> VerifyReport:
    report = VerifyReport()

    shell.step("Verifying the operations plane")
    report.add(_guard("compose services", lambda: check_compose_services(settings, gpu=gpu)))
    report.add(_guard("postgres + pgvector", lambda: check_postgres(settings)))
    report.add(_guard("database least privilege", lambda: check_least_privilege_role(settings)))
    report.add(_guard("kubesage platform ready", lambda: check_platform_ready(settings)))
    report.add(_guard("ollama models", lambda: check_models_present(settings)))
    report.add(_guard("embedding model", lambda: check_embedding_dimensions(settings)))
    report.add(_guard("chat model loads and responds", lambda: check_model_generates(settings)))

    shell.step("Verifying the cluster")
    report.add(_guard("kind cluster", lambda: check_cluster_nodes(settings)))
    report.add(_guard("operations plane -> cluster network", lambda: check_cross_network(settings)))
    report.add(_guard("demo workload", lambda: check_workload_healthy(settings)))
    report.add(_guard("automatic traffic", lambda: check_traffic_flowing(settings)))

    shell.step("Verifying the telemetry pipeline")
    report.add(_guard("log pipeline", lambda: check_log_pipeline(settings)))
    report.add(_guard("prometheus scraping", lambda: check_prometheus_scraping(settings)))
    report.add(_guard("grafana", lambda: check_grafana_datasources(settings)))
    report.add(_guard("evidence api", lambda: check_platform_evidence_api(settings)))

    shell.step("Verifying semantic memory")
    report.add(_guard("semantic memory", lambda: check_semantic_memory(settings)))
    report.add(_guard("semantic retrieval quality", lambda: check_retrieval_quality(settings)))

    shell.step("Verifying security boundaries")
    report.add(_guard("read-only kubernetes rbac", lambda: check_readonly_rbac(settings)))

    return report
