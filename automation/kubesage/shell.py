"""Running external commands and printing readable progress.

Every automation command ultimately shells out to docker, kind or kubectl.
Centralising that here means failures are reported the same way everywhere:
the command that failed, its exit code, and its output - rather than a bare
Python traceback that tells an operator nothing useful.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Sequence
from typing import Any

# Windows terminals in this project support ANSI, but colour is disabled when
# output is redirected so log files stay clean.
_COLOUR = sys.stdout.isatty()


def _paint(code: str, text: str) -> str:
    return f"\033[{code}m{text}\033[0m" if _COLOUR else text


def step(message: str) -> None:
    print(_paint("1;36", f"==> {message}"), flush=True)


def info(message: str) -> None:
    print(f"    {message}", flush=True)


def ok(message: str) -> None:
    print(_paint("32", f"  OK {message}"), flush=True)


def warn(message: str) -> None:
    print(_paint("33", f"  !! {message}"), flush=True)


def fail(message: str) -> None:
    print(_paint("31", f"FAIL {message}"), flush=True)


class CommandError(RuntimeError):
    """An external command exited with a non-zero status."""

    def __init__(self, command: Sequence[str], returncode: int, output: str):
        self.command = list(command)
        self.returncode = returncode
        self.output = output
        super().__init__(
            f"Command failed ({returncode}): {' '.join(command)}\n{output.strip()}"
        )


def run(
    command: Sequence[str],
    *,
    env: dict[str, str] | None = None,
    cwd: str | None = None,
    check: bool = True,
    capture: bool = True,
    timeout: int | None = None,
    stdin_text: str | None = None,
) -> subprocess.CompletedProcess[str]:
    """Run a command, returning the completed process.

    With capture=False the child's output streams straight to the terminal,
    which is what long operations like image pulls need so the operator can
    see progress instead of staring at a frozen prompt.
    """
    result = subprocess.run(  # noqa: S603 - commands are constructed internally
        list(command),
        env=env,
        cwd=cwd,
        input=stdin_text,
        capture_output=capture,
        text=True,
        timeout=timeout,
    )

    if check and result.returncode != 0:
        output = ""
        if capture:
            output = (result.stdout or "") + (result.stderr or "")
        raise CommandError(command, result.returncode, output)

    return result


def run_json(command: Sequence[str], *, env: dict[str, str] | None = None) -> Any:
    """Run a command that prints JSON and return the parsed result."""
    result = run(command, env=env)
    return json.loads(result.stdout)


def which(tool: str) -> str | None:
    return shutil.which(tool)


def http_get(url: str, *, timeout: int = 10) -> tuple[int, str]:
    """Simple GET returning (status, body).

    Connection errors are turned into a status of 0 so callers can treat
    "not listening yet" and "responded with an error" uniformly while waiting
    for a service to come up.
    """
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:  # noqa: S310
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")
    except Exception:
        return 0, ""


def http_post_json(url: str, payload: dict[str, Any], *, timeout: int = 60) -> tuple[int, str]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")
    except Exception as exc:  # pragma: no cover - network failure path
        return 0, str(exc)


def wait_until(
    description: str,
    predicate: Callable[[], bool],
    *,
    timeout: int,
    interval: float = 3.0,
) -> bool:
    """Poll a condition until it holds or the timeout expires.

    Used instead of fixed sleeps so that a fast machine is not punished with
    an arbitrary wait and a slow one still gets the time it needs.
    """
    deadline = time.monotonic() + timeout
    attempt = 0

    while time.monotonic() < deadline:
        attempt += 1
        try:
            if predicate():
                ok(f"{description} (after {attempt} check(s))")
                return True
        except Exception:
            # A predicate that throws simply means "not ready yet".
            pass

        remaining = int(deadline - time.monotonic())
        print(f"    waiting for {description}... {remaining}s left", end="\r", flush=True)
        time.sleep(interval)

    print(" " * 78, end="\r")
    warn(f"timed out waiting for {description} after {timeout}s")
    return False
