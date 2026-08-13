# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the reserved `native` provider.

The native path is intentionally incremental:

1. **Provider boundary** — Core constructs every brain through one factory.
2. **GGUF + runtime configuration** — `BABYAI_NATIVE_MODEL` points to a local GGUF file and `BABYAI_NATIVE_RUNTIME` points to BabyAI's own native shim library. Defaults live under `~/.babyai/models` and `~/.babyai/runtime`.
3. **Runtime loader** — `NativeRuntimeLoader` explicitly loads the BabyAI shim and verifies `BABYAI_NATIVE_ABI_VERSION`. Status/readiness checks do not dynamically load the library.
4. **Stable native shim** — `babyai_native.dll` owns the public C ABI while a pinned llama.cpp build is linked behind it. Upstream llama.cpp structs do not cross the ABI boundary.
5. **Managed model lifecycle** — Python owns runtime/model handles through context-manager-safe wrappers, while the C++ shim also owns child models defensively.
6. **Context lifecycle** — ABI v2 adds opaque context create/destroy plus actual context/batch size queries. Ownership is `runtime -> model -> context` on both C++ and Python sides.
7. **Tokenization** — next: convert UTF-8 prompts to model tokens behind the BabyAI ABI without exposing llama.cpp vocabulary structs.
8. **Decode + sampling** — add bounded decode, sampling, cancellation, context limits, and error translation behind the same stable ABI.
9. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not during ordinary status polling.
- The shim is the only ABI BabyAI Core binds to. Upstream llama.cpp structs and function signatures remain isolated in C++.
- llama.cpp is pinned in CI before native artifacts are built; upgrades are intentional compatibility changes.
- Both native and managed ownership release contexts before models and models before the backend runtime.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics are independent of the selected inference backend.

## BabyAI native ABI v2

The public header is `native/BabyAI.NativeBridge/include/babyai_native.h`. ABI v2 exports:

- `babyai_native_abi_version`
- `babyai_native_runtime_create` / `babyai_native_runtime_destroy`
- `babyai_native_model_open` / `babyai_native_model_close`
- `babyai_native_context_create` / `babyai_native_context_destroy`
- `babyai_native_context_n_ctx` / `babyai_native_context_n_batch`
- `babyai_native_last_error`

`runtime_create` acquires the process-wide llama.cpp backend and `runtime_destroy` releases it through a reference count. The runtime owns all opened model handles defensively and closes them before backend shutdown. A model owns all of its contexts and closes them before releasing the GGUF model.

`context_create` starts from `llama_context_default_params`. Non-zero BabyAI arguments override context size, batch size, and thread count; zero keeps llama.cpp defaults. The shim then calls `llama_init_from_model`. Actual context and batch sizes are queried with `llama_n_ctx` and `llama_n_batch`, because llama.cpp may adjust requested values.

`NativeRuntimeLoader.open_runtime()` is the managed Python entry point. `NativeRuntimeSession.open_model()` returns a managed model handle, and `NativeModelHandle.open_context()` returns a managed context handle. All three levels support `with` and idempotent `close()`.

ABI v2 intentionally does **not** expose tokenization, `llama_decode`, logits, samplers, or text generation yet.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
