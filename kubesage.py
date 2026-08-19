#!/usr/bin/env python3
"""Launcher so every KubeSage command works from the repository root.

    python kubesage.py bootstrap

It exists purely so nobody has to install a package or set PYTHONPATH before
running the automation. The real implementation lives in automation/kubesage/.
"""

from __future__ import annotations

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(REPO_ROOT / "automation"))

if sys.version_info < (3, 11):
    sys.exit(
        f"KubeSage automation needs Python 3.11 or newer (found {sys.version.split()[0]})."
    )

from kubesage.cli import main  # noqa: E402  - import must follow the path setup

if __name__ == "__main__":
    sys.exit(main())
