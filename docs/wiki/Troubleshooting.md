# Troubleshooting

The project treats failures as information. The preferred debugging loop is to identify the exact mechanism before changing architecture or replacing working components.

## 1. Reproduce on an exact build

Before debugging, record:

- commit SHA;
- branch/PR;
- installer or portable artifact used;
- CPU/GPU path selected;
- whether the failure happens every time or intermittently.

This prevents debugging an old binary while reading new source code.

## 2. Separate the layers

Ask which boundary actually failed:

```text
Desktop UI?
Worker transport?
Core command?
Prompt/model generation?
Permission flow?
Windows executor?
Native runtime?
Hardware device?
```

Do not rewrite the layer above a failure before proving the layer below is at fault.

## 3. Read logs before patching

Useful evidence includes:

- Desktop diagnostics;
- native runtime logs;
- GitHub Actions job logs;
- exact exception stack;
- worker protocol output;
- timing/TTFT metrics;
- hardware/device state where relevant.

A failed CI step should be opened at the failing job and command. Re-running a job without understanding a deterministic failure only burns time.

## 4. Prefer minimal fixes

When the root cause is local, fix the local cause. Examples from the project include:

- correcting a WinUI property misuse instead of redesigning the voice button;
- fixing a Python `dataclass(slots=True)` / zero-argument `super()` interaction instead of replacing Workspace PRIMUS;
- keeping protocol v1 compatibility while adding streaming v2 instead of forcing all commands through a new transport at once.

## 5. Hardware-dependent failures

CI cannot validate everything. For microphone, GPU, audio playback or OS-window behavior, add a real-device smoke step.

If microphone VAD fails, inspect actual capture/device selection and measured signal levels before changing thresholds blindly.

If inference is slow, distinguish model-load time, first-token latency and token-generation speed before changing model/runtime settings.

## 6. Do not erase evidence too early

Avoid deleting logs, state files or working code until the failure mechanism is understood. If a state file may be corrupt, preserve a copy or inspect it before resetting it.

## Definition of a good fix

A fix is stronger when it explains:

- what failed;
- why it failed;
- why the change fixes that mechanism;
- what test prevents the same regression;
- what remains unverified.
