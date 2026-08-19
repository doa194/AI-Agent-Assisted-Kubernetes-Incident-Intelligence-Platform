"""Controlled failure scenarios for the demo workload.

Two modules with a deliberate separation:

  definitions   - how to cause and undo each failure. Safe to read anywhere.
  ground_truth  - what a correct investigation should conclude. Private
                  evaluation data that must never reach the AI platform.
"""

from .definitions import SCENARIOS, Scenario, apply, get, names, reset, reset_all

__all__ = ["SCENARIOS", "Scenario", "apply", "get", "names", "reset", "reset_all"]
