# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the reserved `native` provider.

The native path is intentionally incremental:

1. **Provider boundary** — Core constructs every brain through one factory.
2. **GGUF + runtime configuration** — `BABYAI_NATIVE_MODEL` points to a local GGUF file and `BABYAI_NATIVE_RUNTIME` points to BabyAI's own native shim library. Defaults live under `~/.babyai/models` and `~/.babyai/runtime`.
3. **Runtime loader** — `NativeRuntimeLoader` explicitly loads the BabyAI shim and verifies `BABYAI_NATIVE_ABI_VERSION`. Status/readiness checks do not dynamically load the library.
4. **Stable native shim** — `babyai_native.dll` owns the public C ABI while a pinned llama.cpp build is linked behind it. The first ABI exposes backend runtime create/destroy and GGUF model open/close without leaking llama.cpp structs to Python.
5. **Managed model lifecycle** — Python now owns runtime/model handles through context-manager-safe wrappers. Closing a runtime closes every remaining model first, and native model-load errors are translated to `NativeRuntimeError`.
6. **Context lifecycle** — next: create/free a llama context behind an opaque BabyAI handle.
7. **Generation** — tokenization, decode loop, sampling, cancellation, context limits, and error translation behind the same stable ABI.
8. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not during ordinary status polling.
- The shim is the only ABI BabyAI Core binds to. Upstream llama.cpp structs and function signatures remain isolated in C++.
- llama.cpp is pinned in CI before native artifacts are built; upgrades are intentional compatibility changes.
- Managed Python lifecycle always releases model handles before the owning backend runtime.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics are independent of the selected inference backend.

## BabyAI native ABI v1

The public header is `native/BabyAI.NativeBridge/include/babyai_native.h`. ABI v1 exports:

- `babyai_native_abi_version`
- `babyai_native_runtime_create` / `babyai_native_runtime_destroy`
- `babyai_native_model_open` / `babyai_native_model_close`
- `babyai_native_last_error`

`runtime_create` acquires the process-wide llama.cpp backend and `runtime_destroy` releases it through a reference count. `model_open` uses llama.cpp default model parameters plus the requested GPU-layer count, then opens a local GGUF file. The model remains opaque to Python.

`NativeRuntimeLoader.open_runtime()` is the managed Python entry point. `NativeRuntimeSession.open_model()` returns a managed model handle; both support `with` and idempotent `close()`. Native Shim CI exercises this Python path against the actually compiled `babyai_native.dll`, including backend init/free and the expected error path for a missing GGUF model.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
