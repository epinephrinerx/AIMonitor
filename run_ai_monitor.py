"""Entry script for the frozen AI Usage Monitor build.

Separate from `run_app.py`, which still points at the older `claude_monitor`
package: PyInstaller needs a real file to analyse, and one shared entry script
silently decided which of the two packages got built.
"""

from ai_usage_monitor.app import main

if __name__ == "__main__":
    raise SystemExit(main())
