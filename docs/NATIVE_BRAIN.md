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
8. **Decode prefill** — ABI v4 added one bounded initial prompt decode into a fresh context.
9. **Deterministic next token** — ABI v5 samples exactly one greedy next token from the retained final-prompt logits and reports whether it is EOG.
10. **Append decode + token pieces** — ABI v6 appends the exact sampled token at the next position, refreshes logits, and converts token IDs to bounded raw text pieces.
11. **Bounded generation** — next: iterate sample/decode with cancellation, context and output limits, then combine token-piece bytes into UTF-8 text.
12. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not ordinary status polling.
- The shim is the only native ABI BabyAI Core binds to; llama.cpp structs stay inside C++.
- llama.cpp is pinned in CI; upgrades are focused compatibility changes.
- Native/managed ownership releases contexts before models and models before the backend.
- Token buffers are caller-owned and Python caps tokenization at 1,000,000 tokens before allocation.
- Token-piece buffers are caller-owned and Python caps one piece at 1 MiB before allocation.
- Prompt prefill is one-shot and must fit both actual `n_ctx` and actual `n_batch`.
- Any non-zero prefill or append decode result makes the context non-retriable; callers create a fresh context.
- Greedy sampling is deterministic and produces one pending token. Another sample is rejected until that exact token is appended.
- Append decode never accepts an arbitrary replacement token and never writes beyond the actual native context size.
- Token pieces are raw bytes, not independently decoded strings, because a UTF-8 code point may span token boundaries. The generation layer will combine bounded pieces before decoding text.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics remain independent of the inference backend.

## BabyAI native ABI v6

ABI v6 keeps the runtime, model, tokenization, context, prefill and deterministic sampling contracts and adds:

- `babyai_native_model_token_to_piece`
- `babyai_native_context_decode_sampled`

### Prefill

`babyai_native_context_prefill` uses C++-owned temporary vectors to construct one internal `llama_batch`, decodes the prompt, records the committed token count, and requests output only for the final prompt token. No logits cross the BabyAI ABI.

### Greedy next-token sampling

`babyai_native_context_sample_greedy` requires a successful decode state with current logits and no pending sample. It creates a temporary `llama_sampler_init_greedy()` sampler, calls `llama_sampler_sample(sampler, context, -1)`, frees the sampler immediately, validates against `LLAMA_TOKEN_NULL`, and records the selected token as pending. It reports:

- the sampled `int32_t` token ID;
- an integer EOG flag determined by `llama_vocab_is_eog`.

Sampling itself does not mutate KV state or increment `token_count`.

### Append decode

`babyai_native_context_decode_sampled` accepts only the exact pending token returned by the preceding sample. It rejects calls without a pending sample, mismatched token IDs and a full context before entering `llama_decode`.

The shim constructs one BabyAI-owned batch at position `token_count`, sequence 0, requests final output logits, and decodes exactly one token. A successful append increments `token_count`, clears the pending sample and enables another greedy sample. A decode failure marks the context unusable so callers cannot continue from potentially partial native state.

### Token pieces

`babyai_native_model_token_to_piece` is a two-pass caller-owned byte-buffer contract over the pinned llama.cpp vocabulary. The first call reports the required byte count; the second copies the piece bytes into a bounded caller buffer.

The managed `NativeModelHandle.token_to_piece()` returns `bytes` rather than eagerly decoding each piece as UTF-8. This preserves byte sequences when one Unicode code point is split across multiple tokens. Bounded generation will concatenate pieces and decode the combined stream.

The pinned llama.cpp simple example follows the same evaluate → sample → token-to-piece → next-token-decode sequence; BabyAI keeps each operation behind its own stable ABI and explicit state checks.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
