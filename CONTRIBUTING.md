# Contributing

UNIX / BabyAI Core is under active architectural development. External feedback, bug reports, reproductions, and pull requests are welcome, but the canonical codebase remains owner-controlled.

## Changes

1. Open an issue or discussion for architectural or security-sensitive changes before investing in a large patch.
2. Work from a fork / feature branch; do not assume direct write access to the repository.
3. Keep pull requests bounded to one concern where practical.
4. Preserve existing permission, Agent Runtime, streaming, Workspace, memory, and local-first safety boundaries unless the PR explicitly changes and tests that contract.
5. Add regression coverage for bug fixes and user-visible behavior changes.
6. A pull request is not accepted until the repository owner explicitly reviews/merges it.

## Release discipline

The `main` branch is the canonical integration branch. Windows owner smoke testing is required for changes that affect the installed desktop/runtime path when the PR description marks it as an acceptance gate.
