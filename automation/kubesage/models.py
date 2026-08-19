"""Making sure the local models Ollama needs are present.

The reasoning model is about 7.6 GB, so this is normally the slowest part of a
first bootstrap. Pulls are streamed with progress reporting because a silent
twenty minute wait looks indistinguishable from a hang.

Models are pulled through `docker exec` rather than the HTTP API so that the
download progress Ollama already prints is shown directly to the operator.
"""

from __future__ import annotations

import json

from . import shell
from .config import Settings

OLLAMA_CONTAINER = "kubesage-ollama"


def installed_models(settings: Settings) -> set[str]:
    """Model tags currently present in the Ollama volume."""
    status, body = shell.http_get(f"{settings.ollama_url}/api/tags", timeout=15)
    if status != 200:
        return set()

    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return set()

    return {entry.get("name", "") for entry in payload.get("models", [])}


def wait_for_ollama(settings: Settings, *, timeout: int = 180) -> bool:
    return shell.wait_until(
        "Ollama API responding",
        lambda: shell.http_get(f"{settings.ollama_url}/api/tags", timeout=5)[0] == 200,
        timeout=timeout,
        interval=3,
    )


def pull(model: str) -> None:
    shell.info(f"pulling {model} (this can take a while on a first run)")
    shell.run(
        ["docker", "exec", OLLAMA_CONTAINER, "ollama", "pull", model],
        capture=False,
        timeout=7200,
    )


def ensure_models(settings: Settings) -> bool:
    """Pull the chat and embedding models if they are not already present."""
    shell.step("Ensuring local models are available")

    if not wait_for_ollama(settings):
        shell.fail("Ollama did not become reachable; cannot pull models.")
        return False

    present = installed_models(settings)
    required = [settings.chat_model, settings.embedding_model]

    for model in required:
        if model in present:
            shell.ok(f"{model} already present")
            continue
        pull(model)

    present = installed_models(settings)
    missing = [model for model in required if model not in present]

    if missing:
        shell.fail(f"models still missing after pull: {', '.join(missing)}")
        return False

    shell.ok(f"models ready: {', '.join(required)}")
    return True


def probe_generation(settings: Settings, *, timeout: int = 900) -> tuple[bool, str]:
    """Ask the chat model for one short answer to prove it can actually run.

    Having the model file on disk is not the same as being able to load it.
    On a memory constrained machine loading can fail, and it is much better to
    discover that during bootstrap than during the first real investigation.

    Two details matter here and both are properties of Gemma 4 specifically:

      * The /api/chat endpoint is used, not /api/generate. Gemma 4 is a
        reasoning model, and /api/generate puts its output in a hidden
        reasoning channel, returning an empty response that looks exactly like
        a broken model.

      * "think" is set to false. Reasoning tokens are generated at the same
        few tokens per second as everything else, so leaving it on turns a
        two second health check into a minute long one.
    """
    status, body = shell.http_post_json(
        f"{settings.ollama_url}/api/chat",
        {
            "model": settings.chat_model,
            "think": False,
            "stream": False,
            "messages": [{"role": "user", "content": "Reply with the single word: ready"}],
            "options": {"num_predict": 16, "temperature": 0.0},
        },
        timeout=timeout,
    )

    if status != 200:
        return False, f"HTTP {status}: {body[:300]}"

    try:
        content = json.loads(body).get("message", {}).get("content", "").strip()
    except json.JSONDecodeError:
        return False, f"unparsable response: {body[:200]}"

    if not content:
        return False, "the model returned an empty message"

    return True, content


def probe_embedding(settings: Settings, *, timeout: int = 180) -> tuple[bool, int]:
    """Generate one embedding and report how many dimensions came back.

    The dimension count must match Ollama.EmbeddingDimensions in the platform
    configuration, because the database column is sized from it.
    """
    status, body = shell.http_post_json(
        f"{settings.ollama_url}/api/embed",
        {"model": settings.embedding_model, "input": "kubesage embedding dimension probe"},
        timeout=timeout,
    )

    if status != 200:
        return False, 0

    try:
        payload = json.loads(body)
        vectors = payload.get("embeddings") or []
        if not vectors:
            return False, 0
        return True, len(vectors[0])
    except (json.JSONDecodeError, TypeError):
        return False, 0
