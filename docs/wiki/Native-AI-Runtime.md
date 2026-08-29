# Native AI Runtime

UNIX is moving away from depending on an external local-model app as the center of the product. The native runtime exists so the desktop application can own the model lifecycle directly.

## Current baseline

The current local baseline uses a GGUF Qwen3-8B model with a llama.cpp-based native runtime. The release pipeline builds multiple Windows runtime variants, including portable CPU, AVX, AVX2 and Vulkan paths.

The runtime selector chooses an acceleration route and passes the selected native library, GPU-layer configuration and preferred thread count into the resident provider.

## Why a resident provider

Loading a multi-billion-parameter model for every message wastes most of the user's waiting time before generation even begins.

The persistent Desktop worker therefore owns a resident native provider. Once loaded, the model can be reused across turns while mutable stores such as task, memory and permission state are rebuilt/read as needed per command.

This separation gives us two useful properties:

- model startup cost is amortized across the session;
- changing task/memory state does not require reloading the model.

## CPU and Vulkan

CPU fallback matters because UNIX should remain runnable on machines without a suitable GPU path. Vulkan acceleration is used as the practical Windows GPU route for the current baseline.

Performance work should be measured rather than guessed. Useful metrics include:

- model load time;
- native first-token time;
- visible TTFT in the Desktop;
- generation time;
- generated token count;
- memory/VRAM pressure;
- behavior when future STT/TTS models coexist with the main LLM.

## Streaming

Native generation supports a streaming path. The Desktop should not wait for the full answer if validated visible text can already be shown. A safety gate protects the visible stream from internal protocol/tool output before deltas are emitted.

## Future direction

The long-term goal is a self-contained UNIX runtime where the user installs UNIX rather than separately installing and managing another inference application. Compatibility providers can remain useful for diagnostics and development, but they are not the final product boundary.

Canonical implementation notes: `docs/NATIVE_BRAIN.md`.
