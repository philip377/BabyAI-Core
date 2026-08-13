# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the reserved `native` provider.

The native path is intentionally incremental:

1. **Provider boundary** — Core constructs every brain through one factory.
2. **GGUF + runtime configuration** — `BABYAI_NATIVE_MODEL` points to a local GGUF file and `BABYAI_NATIVE_RUNTIME` points to BabyAI's own native shim library. Defaults live under `~/.babyai/models` and `~/.babyai/runtime`.
3. **Runtime loader** — `NativeRuntimeLoader` explicitly loads the BabyAI shim and verifies `BABYAI_NATIVE_ABI_VERSION`. Status/readiness checks do not dynamically load the library.
4. **Stable native shim** — `babyai_native.dll` owns the public C ABI while a pinned llama.cpp build is linked behind it. Upstream llama.cpp structs do not cross the ABI boundary.
5. **Managed model lifecycle** — Python owns runtime/model handles through context-manager-safe wrappers, while the C++ shim also owns child models defensively.
6. **Context lifecycle** — ABI v2 added opaque context create/destroy plus actual context/batch size queries. Ownership is `runtime -> model -> context` on both C++ and Python sides.
7. **Tokenization** — ABI v3 adds bounded two-pass UTF-8 prompt tokenization through the model vocabulary. Token buffers remain caller-owned and Python enforces a hard allocation limit before the second pass.
8. **Decode prefill** — next: submit a bounded prompt token batch to the context without exposing llama.cpp batch structs or sampling yet.
9. **Sampling + text generation** — add bounded generation, token-to-text conversion, cancellation, context limits, and error translation behind the same stable ABI.
10. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not during ordinary status polling.
- The shim is the only ABI BabyAI Core binds to. Upstream llama.cpp structs and function signatures remain isolated in C++.
- llama.cpp is pinned in CI before native artifacts are built; upgrades are intentional compatibility changes.
- Both native and managed ownership release contexts before models and models before the backend runtime.
- Native token buffers are allocated by the caller; the shim never returns heap ownership across the ABI boundary.
- Python refuses tokenization results above `MAX_NATIVE_TOKEN_COUNT` (currently 1,000,000 tokens) before allocating the output buffer.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics are independent of the selected inference backend.

## BabyAI native ABI v3

The public header is `native/BabyAI.NativeBridge/include/babyai_native.h`. ABI v3 exports:

- `babyai_native_abi_version`
- `babyai_native_runtime_create` / `babyai_native_runtime_destroy`
- `babyai_native_model_open` / `babyai_native_model_close`
- `babyai_native_model_tokenize`
- `babyai_native_context_create` / `babyai_native_context_destroy`
- `babyai_native_context_n_ctx` / `babyai_native_context_n_batch`
- `babyai_native_last_error`

`runtime_create` acquires the process-wide llama.cpp backend and `runtime_destroy` releases it through a reference count. The runtime owns all opened model handles defensively and closes them before backend shutdown. A model owns all of its contexts and closes them before releasing the GGUF model.

`context_create` starts from `llama_context_default_params`. Non-zero BabyAI arguments override context size, batch size, and thread count; zero keeps llama.cpp defaults. The shim then calls `llama_init_from_model`. Actual context and batch sizes are queried with `llama_n_ctx` and `llama_n_batch`, because llama.cpp may adjust requested values.

### Tokenization contract

`babyai_native_model_tokenize` converts UTF-8 text to opaque integer token IDs using the vocabulary from the opened GGUF model. The ABI deliberately uses a two-pass caller-owned buffer contract:

1. Call with `tokens_out = NULL` and `token_capacity = 0` to query the required token count through `out_token_count`.
2. Validate that count and allocate the output buffer in the caller.
3. Call again with the buffer to receive `int32_t` token IDs.

The `add_special` and `parse_special` flags map to llama.cpp tokenization behavior. The shim handles llama.cpp's negative required-size return and guards the `INT32_MIN` overflow case before negation.

`NativeModelHandle.tokenize()` performs the two passes for Python callers, UTF-8 encodes the prompt, validates the returned count, enforces the one-million-token safety cap, and returns a plain `list[int]`.

ABI v3 intentionally does **not** expose `llama_decode`, logits, samplers, token pieces, or generated text yet.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
