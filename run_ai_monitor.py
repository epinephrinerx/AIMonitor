"""Entry script for the frozen AI Usage Monitor build.

PyInstaller needs a real file to analyse. This is now the only entry point:
a predecessor package shipped its own launcher alongside this one, and a
single shared script would have silently decided which of the two got built.
"""

from ai_usage_monitor.app import main

if __name__ == "__main__":
    raise SystemExit(main())
