# Installer rollback pointer

The setup keeps versioned application/runtime directories side-by-side. A successful installation validates the new version before switching `current.json`. When an older active version exists, its pointer is stored in `previous.json` so the self-contained setup can restore it with `--rollback` without touching user settings or model data.
