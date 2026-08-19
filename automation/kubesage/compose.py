"""Driving the Docker Compose operations plane.

Compose is always invoked with --env-file versions.env, and the GPU overlay
file is added only when preflight found a usable NVIDIA runtime. Wrapping this
in one place means no caller can forget either detail and end up running a
subtly different stack.
"""

from __future__ import annotations

from . import shell
from .config import (
    COMPOSE_DIR,
    COMPOSE_FILE,
    COMPOSE_GPU_FILE,
    VERSIONS_FILE,
    Settings,
    compose_env,
)


def _base_command(*, gpu: bool) -> list[str]:
    command = [
        "docker",
        "compose",
        "--env-file",
        str(VERSIONS_FILE),
        "-f",
        str(COMPOSE_FILE),
    ]
    if gpu:
        command += ["-f", str(COMPOSE_GPU_FILE)]
    return command


def up(settings: Settings, *, gpu: bool, services: list[str] | None = None, build: bool = False) -> None:
    shell.step("Starting the external operations plane")
    command = _base_command(gpu=gpu) + ["up", "-d"]
    if build:
        command.append("--build")
    if services:
        command += services

    shell.run(command, env=compose_env(), cwd=str(COMPOSE_DIR), capture=False, timeout=2400)
    shell.ok("operations plane started")


def build(settings: Settings, *, gpu: bool) -> None:
    shell.step("Building the KubeSage platform image")
    shell.run(
        _base_command(gpu=gpu) + ["build", "platform"],
        env=compose_env(),
        cwd=str(COMPOSE_DIR),
        capture=False,
        timeout=2400,
    )
    shell.ok("platform image built")


def stop(settings: Settings, *, gpu: bool) -> None:
    shell.step("Stopping the operations plane (data is kept)")
    shell.run(
        _base_command(gpu=gpu) + ["stop"],
        env=compose_env(),
        cwd=str(COMPOSE_DIR),
        capture=False,
    )
    shell.ok("operations plane stopped")


def down(settings: Settings, *, gpu: bool, remove_volumes: bool) -> None:
    shell.step("Removing the operations plane")
    command = _base_command(gpu=gpu) + ["down", "--remove-orphans"]
    if remove_volumes:
        command.append("--volumes")

    shell.run(command, env=compose_env(), cwd=str(COMPOSE_DIR), capture=False, check=False)
    shell.ok("operations plane removed")


def service_states(settings: Settings, *, gpu: bool) -> list[dict[str, str]]:
    """Return name/state/health for each compose service, or [] if none run."""
    result = shell.run(
        _base_command(gpu=gpu) + ["ps", "--format", "json"],
        env=compose_env(),
        cwd=str(COMPOSE_DIR),
        check=False,
    )

    if result.returncode != 0 or not result.stdout.strip():
        return []

    import json

    states: list[dict[str, str]] = []
    # Compose prints one JSON object per line rather than a JSON array.
    for line in result.stdout.strip().splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        states.append(
            {
                "name": entry.get("Name", "?"),
                "state": entry.get("State", "?"),
                "health": entry.get("Health", "") or "n/a",
            }
        )

    return states


def restart_service(settings: Settings, *, gpu: bool, service: str) -> None:
    shell.run(
        _base_command(gpu=gpu) + ["restart", service],
        env=compose_env(),
        cwd=str(COMPOSE_DIR),
        capture=False,
    )
