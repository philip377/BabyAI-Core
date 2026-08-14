# BabyAI uninstall behavior

The per-user uninstaller removes installed application/runtime versions, `current.json`, Desktop and Start Menu shortcuts, and its Windows uninstall registration.

It intentionally preserves user-owned state under `%LOCALAPPDATA%\BabyAI`, including `launch.json` and any current or future model, memory, or user-data directories.
