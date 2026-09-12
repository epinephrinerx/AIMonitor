"""Entry script for the frozen build (PyInstaller needs a real file to analyse)."""

from claude_monitor.app import main

if __name__ == "__main__":
    raise SystemExit(main())
