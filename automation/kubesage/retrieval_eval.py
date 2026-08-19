"""Gold-set evaluation for semantic retrieval.

Checks that a realistic incident description retrieves the runbook that
actually applies to it, using the real embedding model against the real
indexed corpus.

Why a gold set rather than asserting on similarity scores: a distance number
means nothing on its own. What matters operationally is whether the RIGHT
document appears in the top-K results an agent will actually see. Each case
below states the expected document and the run is scored on that.

This is an evaluation, not a unit test. It needs Ollama and a populated
database, so it runs as part of operational verification rather than in the
normal test suite.
"""

from __future__ import annotations

import json
from dataclasses import dataclass

from . import shell
from .config import Settings


@dataclass(frozen=True)
class RetrievalCase:
    """One query and the runbook that should be found for it."""

    name: str

    # Phrased the way an incident title and category would be, because that is
    # what the platform actually searches with.
    query: str

    # Substring identifying the runbook that should be retrieved.
    expected_source_prefix: str

    # A document that must NOT outrank the expected one. These are the
    # confusable pairs - the cases where a plausible-looking wrong answer
    # would send an investigation in the wrong direction.
    must_not_rank_first: str = ""


GOLD_SET: list[RetrievalCase] = [
    RetrievalCase(
        name="payment latency",
        query=(
            "order-api is returning 503 server errors. Category dependency_latency affecting order-api. "
            "Calls to payment-simulator time out after 2 seconds. No pods restarted."
        ),
        expected_source_prefix="dependency-latency",
        # The confusable case: timeouts and errors could look like a crash to
        # a naive match, but nothing restarted.
        must_not_rank_first="pod-crash-loop",
    ),
    RetrievalCase(
        name="container out of memory",
        query=(
            "payment-simulator was terminated for exceeding its memory limit. Category out_of_memory. "
            "Last termination reason OOMKilled with exit code 137."
        ),
        expected_source_prefix="out-of-memory",
        must_not_rank_first="pod-crash-loop",
    ),
    RetrievalCase(
        name="database unavailable",
        query=(
            "Connection failures calling workload-database from notification-worker and order-api. "
            "Category dependency_unavailable. The database deployment has zero ready replicas."
        ),
        expected_source_prefix="database-unavailable",
        must_not_rank_first="dependency-latency",
    ),
    RetrievalCase(
        name="readiness probe failing",
        query=(
            "Every notification-worker pod is failing its readiness probe. Category readiness_failure. "
            "The pods are Running with zero restarts but are not Ready."
        ),
        expected_source_prefix="readiness-failure",
        must_not_rank_first="pod-crash-loop",
    ),
    RetrievalCase(
        name="crash loop",
        query=(
            "order-api restarted 4 times and is in CrashLoopBackOff. Category pod_restart_loop. "
            "The container exited with a non-zero code shortly after starting."
        ),
        expected_source_prefix="pod-crash-loop",
        must_not_rank_first="out-of-memory",
    ),
]


def _embed(settings: Settings, text: str) -> list[float] | None:
    # Long timeout on purpose. Only one model is resident at a time, so an
    # embed request issued while an investigation is running waits for that
    # generation and then for a model swap. Measured worst case is a few
    # minutes; a shorter timeout reports a healthy system as broken.
    status, body = shell.http_post_json(
        f"{settings.ollama_url}/api/embed",
        {"model": settings.embedding_model, "input": text},
        timeout=300,
    )

    if status != 200:
        return None

    try:
        return json.loads(body)["embeddings"][0]
    except (KeyError, IndexError, json.JSONDecodeError):
        return None


def _search(vector: list[float], top_k: int) -> list[tuple[str, float]]:
    """Query semantic memory directly, mirroring what the platform does."""
    literal = "[" + ",".join(f"{v:.6f}" for v in vector) + "]"

    result = shell.run(
        [
            "docker", "exec", "kubesage-postgres",
            "psql", "-U", "kubesage_owner", "-d", "kubesage", "-tAF", "|", "-c",
            "SELECT source_ref, (embedding <=> '" + literal + "'::vector) AS distance "
            "FROM semantic_memory WHERE kind = 'runbook' "
            "ORDER BY embedding <=> '" + literal + "'::vector LIMIT " + str(top_k),
        ],
        check=False,
    )

    rows = []
    for line in (result.stdout or "").strip().splitlines():
        if "|" not in line:
            continue
        source_ref, _, distance = line.rpartition("|")
        try:
            rows.append((source_ref, float(distance)))
        except ValueError:
            continue

    return rows


def run_evaluation(settings: Settings, *, top_k: int = 5) -> bool:
    """Score every gold case. Returns True when all of them pass."""
    shell.step(f"Semantic retrieval evaluation ({len(GOLD_SET)} gold cases, top-{top_k})")

    passed = 0

    for case in GOLD_SET:
        vector = _embed(settings, case.query)

        if vector is None:
            shell.fail(f"{case.name}: could not embed the query")
            continue

        results = _search(vector, top_k)

        if not results:
            shell.fail(f"{case.name}: no results returned")
            continue

        hit_rank = next(
            (index + 1 for index, (ref, _) in enumerate(results)
             if ref.startswith(case.expected_source_prefix)),
            None,
        )

        top_ref = results[0][0]
        wrong_first = (
            case.must_not_rank_first
            and top_ref.startswith(case.must_not_rank_first)
        )

        if hit_rank is None:
            shell.fail(
                f"{case.name}: expected '{case.expected_source_prefix}' in top-{top_k}, "
                f"got {[r for r, _ in results]}"
            )
        elif wrong_first:
            shell.fail(
                f"{case.name}: '{case.must_not_rank_first}' ranked first, "
                f"which would send the investigation the wrong way"
            )
        else:
            passed += 1
            shell.ok(
                f"{case.name}: '{case.expected_source_prefix}' at rank {hit_rank} "
                f"(distance {results[hit_rank - 1][1]:.3f})"
            )

    shell.info(f"{passed}/{len(GOLD_SET)} gold retrieval cases passed")
    return passed == len(GOLD_SET)
