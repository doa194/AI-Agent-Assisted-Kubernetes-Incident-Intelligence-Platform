"""Allows `python -m kubesage` from inside the automation directory."""

import sys

from .cli import main

sys.exit(main())
