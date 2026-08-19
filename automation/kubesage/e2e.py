"""The two end-to-end workflows that prove the project does what it claims.

Deliberately only two. End-to-end tests here are slow - a single investigation
takes several minutes on a local 12B model - and everything they would
otherwise duplicate is already covered more cheaply by unit, integration and
component tests. What cannot be covered anywhere else is whether the WHOLE
chain works against real infrastructure, and that is all these do:

  1. a clean environment produces an automatic startup report without anyone
     asking for one;

  2. a controlled failure is detected deterministically, investigated by the
     three agents, and produces a report whose root cause matches the private
     ground truth and whose citations resolve to real evidence.

The second is the project's central claim, tested end to end against a real
model, a real cluster, and real telemetry.
"""

from __future__ import annotations

import json
import time
import urllib.request
from dataclasses import dataclass, field

from . import cluster, scenarios, shell
from .config import Settings
from .scenarios import ground_truth


@dataclass
class E2EResult:
    name: str
    passed: bool
    detail: str = ""
    checks: list[tuple[str, bool, str]] = field(default_factory=list)


def _get(url: str, timeout: int = 60):
    status, body = shell.http_get(url, timeout=timeout)
    if status != 200:
        return None
    try:
        return json.loads(body)
    except json.JSONDecodeError:
        return None


def _post(url: str, timeout: int = 240):
    request = urllib.request.Request(url, data=b"", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return json.loads(response.read().decode("utf-8"))
    except Exception:
        return None


def _reset_platform_state(settings: Settings) -> None:
    """Clear incidents, queued work and reports for a clean run.

    Runbook embeddings are deliberately KEPT: re-indexing them costs model time
    and they are static input, not run state.
    """
    shell.run(
        [
            "docker", "exec", "kubesage-postgres",
            "psql", "-U", "kubesage_owner", "-d", "kubesage", "-c",
            "TRUNCATE incidents CASCADE; TRUNCATE work_items; DELETE FROM reports;",
        ],
        check=False,
    )


# ---------------------------------------------------------------------------
# Workflow 1: clean start produces an automatic cluster report
# ---------------------------------------------------------------------------

def clean_startup_analysis(settings: Settings, *, timeout_seconds: int = 900) -> E2EResult:
    shell.step("E2E 1: a clean environment reports on itself, unprompted")

    scenarios.reset_all(settings)
    _reset_platform_state(settings)

    # Restarting the platform is what triggers the startup analysis; nothing
    # else asks for it.
    shell.info("restarting the platform to trigger its startup analysis")
    shell.run(["docker", "restart", "kubesage-platform"], check=False, capture=False)

    shell.wait_until(
        "platform to come back",
        lambda: shell.http_get(f"{settings.platform_url}/health/live", timeout=5)[0] == 200,
        timeout=180,
        interval=5,
    )

    def startup_report_exists() -> bool:
        reports = _get(f"{settings.platform_url}/reports?limit=20", timeout=30) or []
        return any(r.get("kind") == "startup-analysis" for r in reports)

    found = shell.wait_until(
        "an automatic startup report",
        startup_report_exists,
        timeout=timeout_seconds,
        interval=20,
    )

    if not found:
        return E2EResult(
            "clean startup analysis", False,
            f"no startup report was produced within {timeout_seconds}s",
        )

    reports = _get(f"{settings.platform_url}/reports?limit=20", timeout=30) or []
    report = next(r for r in reports if r.get("kind") == "startup-analysis")

    checks = [
        ("a report was generated with nobody asking", True, report["title"][:70]),
        (
            "it states an overall cluster status",
            report.get("severity") in {"healthy", "degraded", "unhealthy"},
            f"status={report.get('severity')}",
        ),
        (
            "it cites evidence it actually examined",
            len(report.get("evidenceIds") or []) > 0,
            f"{len(report.get('evidenceIds') or [])} evidence id(s)",
        ),
        (
            "it is a cluster report, not an incident report",
            report.get("incidentId") is None,
            "no incident attached, as expected",
        ),
    ]

    for label, ok, detail in checks:
        (shell.ok if ok else shell.fail)(f"{label}: {detail}")

    return E2EResult(
        "clean startup analysis",
        all(ok for _, ok, _ in checks),
        report["title"][:80],
        checks,
    )


# ---------------------------------------------------------------------------
# Workflow 2: controlled incident -> detection -> agents -> grounded report
# ---------------------------------------------------------------------------

def incident_investigation(
    settings: Settings,
    scenario_name: str = "payment-latency",
    *,
    timeout_seconds: int = 1800,
) -> E2EResult:
    shell.step(f"E2E 2: '{scenario_name}' from failure to evidence-backed report")

    expected = ground_truth.expected_for(scenario_name)
    scenario = scenarios.get(scenario_name)

    scenarios.reset_all(settings)
    _reset_platform_state(settings)

    scenarios.apply(settings, scenario)

    try:
        shell.info(f"waiting {scenario.signal_delay_seconds}s for the failure to reach telemetry")
        time.sleep(scenario.signal_delay_seconds)

        # Detection runs on its own schedule; forcing a pass only removes up to
        # a minute of waiting from a test that already takes many.
        _post(f"{settings.platform_url}/analysis/run", timeout=240)

        incidents = _get(f"{settings.platform_url}/incidents", timeout=60) or []

        if not incidents:
            return E2EResult(
                "incident investigation", False,
                "detection produced no incidents, so there was nothing to investigate",
            )

        shell.ok(f"detection raised {len(incidents)} incident(s) with no model involved")

        # Wait for an incident report naming the workload that is genuinely at
        # fault. Other incidents from the same outage are investigated too;
        # this waits for the one that matters.
        def culprit_report_exists() -> bool:
            reports = _get(f"{settings.platform_url}/reports?limit=30", timeout=30) or []
            return any(
                r.get("kind") == "incident"
                and expected.root_cause_workload.lower() in _report_text(r)
                for r in reports
            )

        found = shell.wait_until(
            f"a report identifying '{expected.root_cause_workload}' as the cause",
            culprit_report_exists,
            timeout=timeout_seconds,
            interval=30,
        )

        reports = _get(f"{settings.platform_url}/reports?limit=30", timeout=30) or []
        incident_reports = [r for r in reports if r.get("kind") == "incident"]

        if not incident_reports:
            return E2EResult(
                "incident investigation", False,
                f"no incident report was produced within {timeout_seconds}s",
            )

        report = next(
            (r for r in incident_reports
             if expected.root_cause_workload.lower() in _report_text(r)),
            incident_reports[0],
        )

        checks = _score(settings, report, expected, found)

        for label, ok, detail in checks:
            (shell.ok if ok else shell.fail)(f"{label}: {detail}")

        return E2EResult(
            "incident investigation",
            all(ok for _, ok, _ in checks),
            report.get("likelyRootCause", "")[:90],
            checks,
        )
    finally:
        # Always reset, even on failure: a leftover fault would contaminate
        # every later run and look like a new incident.
        scenarios.reset(settings, scenario)


def _report_text(report: dict) -> str:
    return " ".join(
        str(report.get(field) or "")
        for field in ("title", "summary", "impact", "likelyRootCause")
    ).lower()


def _score(settings: Settings, report: dict, expected, found_culprit: bool):
    """Score a report against ground truth the agents never saw."""
    text = _report_text(report)

    checks = [
        (
            f"names the true root cause workload '{expected.root_cause_workload}'",
            expected.root_cause_workload.lower() in text,
            expected.root_cause_workload if found_culprit else "not named",
        ),
        (
            "root cause category is the right kind of problem",
            _category_matches(report.get("rootCauseCategory") or "", expected.root_cause_category),
            f"reported '{report.get('rootCauseCategory')}', expected something like "
            f"'{expected.root_cause_category}'",
        ),
        (
            "does not blame a workload that was only a victim",
            not _blames_victim(text, expected.incorrect_root_cause_workloads),
            "no victim workload named as the cause",
        ),
    ]

    # Every citation must resolve to stored evidence. This is the claim that
    # separates an evidence-backed report from a plausible-sounding one.
    detail = _get(f"{settings.platform_url}/reports/{report['id']}/evidence", timeout=60)
    cited = len(report.get("evidenceIds") or [])
    resolved = len(detail.get("citedEvidence", [])) if detail else 0

    checks.append((
        "every cited evidence id resolves to real stored evidence",
        cited > 0 and cited == resolved,
        f"{resolved}/{cited} resolved",
    ))

    # For a scenario where Kubernetes stays healthy, a report claiming a crash
    # has misread the evidence entirely.
    if not expected.expect_kubernetes_disruption:
        checks.append((
            "does not invent a pod failure (nothing restarted in this scenario)",
            not any(t in text for t in ("crashloop", "oomkill", "pod crash", "restarted repeatedly")),
            "no Kubernetes fault claimed, correctly",
        ))

    return checks


def _category_matches(reported: str, expected: str) -> bool:
    """Compare root cause categories by meaning rather than exact wording.

    Models phrase categories differently between runs, and asserting on exact
    strings would test the wording rather than the diagnosis. Shared
    significant words are enough to tell "downstream dependency slow" from
    "container memory limit exceeded".
    """
    reported_words = set(reported.lower().replace("-", "_").split("_"))
    expected_words = set(expected.lower().replace("-", "_").split("_"))

    noise = {"the", "a", "of", "and", "is", "was"}
    return bool((reported_words & expected_words) - noise)


def _blames_victim(text: str, victims: list[str]) -> bool:
    return any(
        phrase in text
        for victim in victims
        for phrase in (
            f"root cause is {victim}",
            f"root cause is the {victim}",
            f"caused by {victim}",
            f"{victim} is the root cause",
        )
    )


def run_all(settings: Settings) -> list[E2EResult]:
    results = [
        clean_startup_analysis(settings),
        incident_investigation(settings),
    ]

    shell.step("End-to-end summary")
    for result in results:
        marker = "PASS" if result.passed else "FAIL"
        print(f"    [{marker}] {result.name}: {result.detail}")

    return results
