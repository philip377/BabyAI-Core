# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the reserved `native` provider.

The native path is intentionally incremental:

1. **Provider boundary** — Core constructs every brain through one factory.
2. **GGUF + runtime configuration** — local model/runtime paths live behind BabyAI configuration.
3. **Runtime loader** — `NativeRuntimeLoader` explicitly loads and validates BabyAI's native ABI.
4. **Stable native shim** — `babyai_native.dll` statically links a pinned llama.cpp while hiding upstream structs from Python.
5. **Managed model lifecycle** — runtime/model handles are context-manager-safe and defensively owned in C++ too.
6. **Context lifecycle** — ABI v2 added opaque context create/destroy and actual context/batch size queries.
7. **Tokenization** — ABI v3 added bounded two-pass UTF-8 -> token ID conversion with caller-owned buffers.
8. **Decode prefill** — ABI v4 adds one bounded initial prompt decode into a fresh context.
9. **Sampling + text generation** — next: sample a single next token from the retained final-prompt logits, then add bounded iterative generation and token-to-text conversion.
10. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not ordinary status polling.
- The shim is the only native ABI BabyAI Core binds to; llama.cpp structs stay inside C++.
- llama.cpp is pinned in CI; upgrades are focused compatibility changes.
- Native/managed ownership releases contexts before models and models before the backend.
- Token buffers are caller-owned and Python caps tokenization at 1,000,000 tokens before allocation.
- ABI v4 prefill is deliberately one-shot and must fit both the actual `n_ctx` and actual `n_batch` of a fresh context.
- If `llama_decode` returns any non-zero result, that context is not retried because upstream documents that some failure/abort paths may leave partial memory state. Create a fresh context instead.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics remain independent of the inference backend.

## BabyAI native ABI v4

The public header is `native/BabyAI.NativeBridge/include/babyai_native.h`. ABI v4 exports the existing runtime/model/tokenization/context lifecycle plus:

- `babyai_native_context_prefill`
- `babyai_native_context_token_count`

### Tokenization

`babyai_native_model_tokenize` remains a two-pass caller-owned contract: first query the required count, validate/allocate in the caller, then receive `int32_t` token IDs. Python performs UTF-8 encoding, count validation and the allocation safety cap.

### Decode prefill

`babyai_native_context_prefill` accepts a non-empty caller-owned `int32_t` token array and only operates on a fresh context. Before allocating a temporary batch it verifies the token count fits both `llama_n_ctx()` and `llama_n_batch()`.

The temporary `llama_batch` never crosses the BabyAI ABI. Its token, position, sequence and output arrays are backed by C++ `std::vector`, so allocation failures are translated through BabyAI errors instead of depending on llama.cpp's raw-malloc batch helper. Sequence 0 and explicit positions `0..N-1` are used for this first prompt. Only the last prompt token requests an internal logits output so the next sampling milestone can consume it later; logits are not exposed by ABI v4.

On successful `llama_decode`, the native context records the committed token count. On decode failure the context records that a prefill attempt occurred and rejects retries; callers should destroy it and create a fresh context.

`NativeContextHandle.prefill()` mirrors these limits in Python before crossing the ABI, validates token IDs, calls native prefill, and verifies the committed count returned by `babyai_native_context_token_count`.

ABI v4 intentionally does **not** expose logits, samplers, token pieces, or generated text.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
