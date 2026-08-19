"""Command line entry point for every KubeSage operation.

Run from the repository root:

    python kubesage.py bootstrap     create everything from nothing
    python kubesage.py start         start an environment that already exists
    python kubesage.py stop          stop it, keeping all data
    python kubesage.py cleanup       delete everything this project created
    python kubesage.py status        what is running right now
    python kubesage.py verify        prove the environment actually works

Only the Python standard library is used, so no virtual environment or pip
install is required before the first command works.
"""

from __future__ import annotations

import argparse
import sys

from . import (
    cluster,
    e2e,
    compose,
    models,
    preflight,
    scenario_checks,
    scenarios,
    shell,
    status,
    verify,
    workloads,
)
from .config import BUILD_DIR, COMPOSE_GENERATED_DIR, Settings, load_settings


def _detect_gpu(settings: Settings) -> bool:
    """Decide whether the GPU compose overlay should be included.

    Every command needs this, and it must give the same answer each time or
    compose would treat the stack as a different configuration and recreate
    containers unnecessarily.
    """
    try:
        result = shell.run(["docker", "info", "--format", "{{json .Runtimes}}"], check=False)
        return "nvidia" in result.stdout
    except Exception:
        return False


# --------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------

def cmd_preflight(settings: Settings, args: argparse.Namespace) -> int:
    result = preflight.run_preflight(settings, expect_ports_free=False)
    if result.passed:
        shell.ok("preflight passed")
        return 0
    shell.fail("preflight failed")
    return 1


def cmd_bootstrap(settings: Settings, args: argparse.Namespace) -> int:
    """Create the whole local environment from a clean machine."""
    check = preflight.run_preflight(settings, expect_ports_free=not cluster.exists(settings))
    if not check.passed:
        shell.fail("Preflight failed. Fix the problems above and run bootstrap again.")
        return 1

    gpu = check.gpu_available
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    COMPOSE_GENERATED_DIR.mkdir(parents=True, exist_ok=True)

    # The cluster is created first because the operations plane's
    # configuration refers to ports that only exist once the cluster is up.
    cluster.create(settings)
    if not cluster.wait_ready(settings):
        shell.fail("Cluster nodes did not become Ready.")
        return 1

    # Models are pulled while nothing else needs the machine, because this is
    # the slowest step of a first bootstrap by a wide margin.
    compose.up(settings, gpu=gpu, services=["postgres", "ollama"])

    if not models.ensure_models(settings):
        return 1

    # The workload goes in before the platform so that by the time the
    # platform starts its warm-up, telemetry is already being produced.
    workloads.build_images(settings)
    workloads.load_images(settings)
    workloads.deploy(settings)
    workloads.wait_ready(settings)

    compose.up(settings, gpu=gpu, build=True)

    shell.step("Bootstrap complete")
    shell.info("Run 'python kubesage.py verify' to confirm everything works.")
    shell.info("Run 'python kubesage.py status' to see endpoints.")
    return 0


def cmd_start(settings: Settings, args: argparse.Namespace) -> int:
    gpu = _detect_gpu(settings)

    if not cluster.exists(settings):
        shell.fail(
            f"Kind cluster '{settings.cluster_name}' does not exist. Run 'bootstrap' first."
        )
        return 1

    compose.up(settings, gpu=gpu)
    cluster.wait_ready(settings)
    return 0


def cmd_stop(settings: Settings, args: argparse.Namespace) -> int:
    # The cluster is intentionally left alone: stopping and starting Kind
    # nodes is slow and error prone, and keeping them running costs little.
    compose.stop(settings, gpu=_detect_gpu(settings))
    shell.info("The Kind cluster is still running. Use 'cleanup' to remove it.")
    return 0


def cmd_cleanup(settings: Settings, args: argparse.Namespace) -> int:
    gpu = _detect_gpu(settings)

    shell.step("Cleaning up everything KubeSage created")
    if not args.keep_models:
        shell.info("model volume will be removed; the next bootstrap re-downloads ~8 GB")

    compose.down(settings, gpu=gpu, remove_volumes=not args.keep_models)
    cluster.delete(settings)

    if BUILD_DIR.exists():
        import shutil

        shutil.rmtree(BUILD_DIR, ignore_errors=True)
        shell.ok("removed generated build artefacts")

    shell.ok("cleanup complete")
    return 0


def cmd_status(settings: Settings, args: argparse.Namespace) -> int:
    status.show_status(settings, gpu=_detect_gpu(settings))
    return 0


def cmd_scenario(settings: Settings, args: argparse.Namespace) -> int:
    if args.scenario_command == "list":
        shell.step("Available failure scenarios")
        for name in scenarios.names():
            scenario = scenarios.get(name)
            print(f"    {name:<22} target={scenario.target_deployment}")
            print(f"    {'':<22} {scenario.summary}")
            print()
        return 0

    if args.scenario_command == "check":
        only = None if args.name == "all" else [args.name]
        results = scenario_checks.check_all(settings, only=only)
        return 0 if all(r.passed for r in results) else 1

    if args.scenario_command == "reset" and args.name == "all":
        return 0 if scenarios.reset_all(settings) else 1

    try:
        scenario = scenarios.get(args.name)
    except KeyError as exc:
        shell.fail(str(exc))
        return 1

    if args.scenario_command == "run":
        scenarios.apply(settings, scenario)
        return 0

    return 0 if scenarios.reset(settings, scenario) else 1


def cmd_workload(settings: Settings, args: argparse.Namespace) -> int:
    """Rebuild and redeploy the demo workload after a code change."""
    workloads.build_images(settings)
    workloads.load_images(settings)
    workloads.deploy(settings)

    # An image with the same tag is not noticed by Kubernetes, so a restart is
    # required for the new build to actually be used.
    shell.step("Restarting workload deployments to pick up the new images")
    for service in workloads.SERVICES:
        cluster.kubectl(
            settings, "rollout", "restart", f"deployment/{service}",
            "-n", settings.workload_namespace, capture=False,
        )

    return 0 if workloads.wait_ready(settings) else 1


def cmd_e2e(settings: Settings, args: argparse.Namespace) -> int:
    """Run the two critical end-to-end workflows against the live system."""
    results = e2e.run_all(settings)
    return 0 if all(r.passed for r in results) else 1


def cmd_verify(settings: Settings, args: argparse.Namespace) -> int:
    report = verify.run_verification(settings, gpu=_detect_gpu(settings))

    shell.step("Verification summary")
    print(f"    {report.summary()}")

    if report.passed:
        shell.ok("environment verified")
        return 0

    shell.fail("environment verification failed")
    return 1


# --------------------------------------------------------------------------
# Argument parsing
# --------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="kubesage",
        description="Local automation for the KubeSage incident intelligence platform.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser(
        "preflight", help="check that this machine has what KubeSage needs"
    ).set_defaults(handler=cmd_preflight)

    subparsers.add_parser(
        "bootstrap", help="create the cluster and operations plane from scratch"
    ).set_defaults(handler=cmd_bootstrap)

    subparsers.add_parser(
        "start", help="start an environment that has already been bootstrapped"
    ).set_defaults(handler=cmd_start)

    subparsers.add_parser(
        "stop", help="stop the operations plane, keeping all data"
    ).set_defaults(handler=cmd_stop)

    cleanup = subparsers.add_parser(
        "cleanup", help="delete the cluster, containers and volumes"
    )
    cleanup.add_argument(
        "--keep-models",
        action="store_true",
        help="keep the Ollama model volume so the next bootstrap does not re-download it",
    )
    cleanup.set_defaults(handler=cmd_cleanup)

    subparsers.add_parser(
        "status", help="show what is currently running"
    ).set_defaults(handler=cmd_status)

    subparsers.add_parser(
        "verify", help="run operational checks against the running environment"
    ).set_defaults(handler=cmd_verify)

    subparsers.add_parser(
        "workload", help="rebuild, reload and restart the demo workload images"
    ).set_defaults(handler=cmd_workload)

    subparsers.add_parser(
        "e2e",
        help="run the two critical end-to-end workflows (slow: uses the real model)",
    ).set_defaults(handler=cmd_e2e)

    scenario = subparsers.add_parser(
        "scenario", help="run or reset a controlled failure scenario"
    )
    scenario_sub = scenario.add_subparsers(dest="scenario_command", required=True)

    scenario_sub.add_parser("list", help="show the available scenarios")

    scenario_run = scenario_sub.add_parser("run", help="start a failure scenario")
    scenario_run.add_argument("name", choices=scenarios.names())

    scenario_reset = scenario_sub.add_parser("reset", help="undo a failure scenario")
    scenario_reset.add_argument(
        "name",
        choices=[*scenarios.names(), "all"],
        help="scenario to reset, or 'all' to clear every injected fault",
    )

    scenario_check = scenario_sub.add_parser(
        "check",
        help="run scenarios end to end and confirm they produce their expected telemetry and reset cleanly",
    )
    scenario_check.add_argument(
        "name",
        nargs="?",
        default="all",
        choices=[*scenarios.names(), "all"],
        help="scenario to check, or 'all' (default)",
    )

    scenario.set_defaults(handler=cmd_scenario)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    settings = load_settings()

    try:
        return args.handler(settings, args)
    except shell.CommandError as exc:
        shell.fail(str(exc))
        return exc.returncode or 1
    except KeyboardInterrupt:
        shell.warn("interrupted")
        return 130


if __name__ == "__main__":
    sys.exit(main())
