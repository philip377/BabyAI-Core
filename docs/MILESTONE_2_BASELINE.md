# Milestone 2 performance and stability baseline

Captured on 2026-08-22 from the current installed PR #118 candidate on Windows 10,
using `Qwen3-8B-Q4_K_M.gguf`, an RTX 2060 SUPER 8 GB, 8 logical CPU threads,
4 selected native threads, and the Vulkan runtime. These numbers are a regression
baseline, not a performance target and not a reason to increase request timeouts.

## Native route and latency

- Route: `mode=vulkan`, `n_gpu_layers=-1`.
- Cold model load: 3,531 ms.
- Cold request: 7,359 ms end to end; 765 ms prefill for 1,433 prompt tokens;
  329 ms from prefill completion to first sampled token; 43.33 generated tokens/s.
- Warm requests: 3,562-4,031 ms end to end; 656-1,125 ms prefill for
  1,217-1,335 prompt tokens; 156-234 ms to first sampled token;
  44.77-46.03 generated tokens/s.
- Deterministic Desktop listing: 15 ms to create the one-shot approval and 16 ms
  to approve, execute, revoke, and answer. No native generation ran.

`native-runtime.log` already records provider selection, model load, tokenize,
context sizing, prefill, first token, generation duration, throughput, stop reason,
and total chat duration. Stage 1 also adds process working-set, private-memory, and
peak-working-set fields to provider and chat trace events.

## Memory snapshot

The installed app was idle immediately after the requests above:

- Desktop UI: 171.3 MiB working set, 113.2 MiB private bytes.
- Resident native Python worker: 5,109.1 MiB working set, 5,107.3 MiB private bytes.
- Lightweight companion Python process: 46.2 MiB working set, 29.7 MiB private bytes.
- System-wide GPU memory at the sampling instant: 5,088 MiB of 8,192 MiB.

Windows WDDM did not expose reliable per-process dedicated/shared GPU attribution to
the non-elevated sampler, so the GPU number must not be treated as BabyAI-only VRAM.
The new process counters intentionally report RAM only rather than inventing a VRAM
breakdown.

## Stability gates

- Ordinary Russian chat keeps the local tool catalog out of the prompt and does not
  add an English parenthetical translation unless requested.
- Hallucinated or intent-incompatible tool JSON is neither executed nor shown.
- `system.info`, `process.list`, `filesystem.list`, and `filesystem.read` remain
  capability-gated. Windows process listing uses only fixed `tasklist.exe` arguments,
  has a five-second timeout, and returns at most 200 entries.
- One-shot approval is now executor-local and never written to `permissions.json`;
  cleanup also occurs when execution raises.
- `~/Desktop` resolves through the Windows Known Folder API, including redirected
  OneDrive desktops.

Re-capture this baseline only from a newly built artifact and keep the model, prompt,
route, performance profile, and cold/warm state in the comparison record.
