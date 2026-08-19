"""A quick, read-only picture of what is currently running.

Unlike `verify`, this never probes the model or attempts privileged
operations. It is the command to run when you just want to know whether the
environment is up and where to point a browser.
"""

from __future__ import annotations

from . import cluster, compose, shell
from .config import Settings


def _endpoint_line(label: str, url: str, healthy: bool | None) -> str:
    if healthy is None:
        marker = "  ? "
    elif healthy:
        marker = " up "
    else:
        marker = "down"
    return f"    [{marker}] {label:<22} {url}"


def show_status(settings: Settings, *, gpu: bool) -> None:
    shell.step("Operations plane (Docker Compose)")
    services = compose.service_states(settings, gpu=gpu)

    if not services:
        shell.info("no compose services are running")
    else:
        for service in services:
            print(f"    {service['name']:<24} {service['state']:<12} health={service['health']}")

    shell.step("Cluster (Kind)")
    if not cluster.exists(settings):
        shell.info(f"cluster '{settings.cluster_name}' does not exist")
    else:
        try:
            for name, ready, role in cluster.node_status(settings):
                print(f"    {name:<32} Ready={ready:<6} role={role}")
        except Exception as exc:
            shell.warn(f"cluster exists but is not answering: {exc}")

    shell.step("Endpoints")
    checks = [
        ("KubeSage API", f"{settings.platform_url}/health/ready"),
        ("Grafana", f"{settings.grafana_url}/api/health"),
        ("Loki", f"{settings.loki_url}/ready"),
        ("Prometheus", f"{settings.prometheus_url}/-/healthy"),
        ("Demo gateway", f"{settings.gateway_url}/health/ready"),
        ("Ollama", f"{settings.ollama_url}/api/tags"),
    ]

    for label, url in checks:
        status, _ = shell.http_get(url, timeout=4)
        print(_endpoint_line(label, url, healthy=status == 200 if status else False))

    shell.step("Dependency health (including degradations readiness hides)")
    status, body = shell.http_get(f"{settings.platform_url}/health/detail", timeout=20)

    if status == 200:
        import json

        try:
            for check in json.loads(body).get("checks", []):
                marker = "ok" if check["status"] == "Healthy" else check["status"].lower()
                detail = check.get("description") or check.get("error") or ""
                print(f"    [{marker:>9}] {check['name']:<12} {detail[:90]}")
        except (json.JSONDecodeError, KeyError):
            shell.warn("could not read the health detail response")
    else:
        shell.info("platform not answering; dependency health unavailable")

    shell.step("Useful addresses")
    print(f"    Grafana dashboards   {settings.grafana_url} (anonymous viewer access)")
    print(f"    KubeSage incidents   {settings.platform_url}/incidents")
    print(f"    Latest report        {settings.platform_url}/reports/latest")
