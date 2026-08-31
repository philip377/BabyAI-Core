# Security Policy

UNIX / BabyAI Core is an actively developed local-first assistant with access to protected local capabilities. Security reports should therefore avoid public disclosure of exploitable details until the issue is understood and fixed.

## Reporting

Please report suspected vulnerabilities privately to the repository owner rather than opening a public issue containing exploit steps, secrets, credentials, personal data, or unsafe payloads.

## Repository integrity

- protected local actions are deny-by-default
- model output is not treated as proof that an external action occurred
- local machine facts must come from executed tools / observations
- release artifacts are built through GitHub Actions and verified with hashes
- direct changes to the canonical `main` branch should remain owner-controlled

This project is pre-1.0; security boundaries may evolve, but they should fail closed rather than silently broaden access.
