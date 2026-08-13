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
10. **Append decode + token text** — next: decode the selected token at the next position, refresh logits, and convert token IDs to UTF-8 pieces.
11. **Bounded generation** — iterate sample/decode with cancellation and context limits, then add configurable sampling.
12. **Packaging** — ship the tested shim and model next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not ordinary status polling.
- The shim is the only native ABI BabyAI Core binds to; llama.cpp structs stay inside C++.
- llama.cpp is pinned in CI; upgrades are focused compatibility changes.
- Native/managed ownership releases contexts before models and models before the backend.
- Token buffers are caller-owned and Python caps tokenization at 1,000,000 tokens before allocation.
- Prompt prefill is one-shot and must fit both actual `n_ctx` and actual `n_batch`.
- Any non-zero prefill decode result makes that context non-retriable; callers create a fresh context.
- ABI v5 sampling is deliberately deterministic greedy sampling and is one-shot for the current logits. It does not append the sampled token to KV state.
- Ollama remains the default until native generation passes Core, Windows Desktop, Native Shim CI, and manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics remain independent of the inference backend.

## BabyAI native ABI v5

ABI v5 keeps the runtime, model, tokenization, context and prefill contracts and adds:

- `babyai_native_context_sample_greedy`

### Prefill

`babyai_native_context_prefill` uses C++-owned temporary vectors to construct one internal `llama_batch`, decodes the prompt, records the committed token count, and requests output only for the final prompt token. No logits cross the BabyAI ABI.

### Greedy next-token sampling

`babyai_native_context_sample_greedy` requires a successful prefill and may be called only once for the current logits. It creates a temporary `llama_sampler_init_greedy()` sampler, calls `llama_sampler_sample(sampler, context, -1)` to sample from the final output of the last evaluation, frees the sampler immediately, validates against `LLAMA_TOKEN_NULL`, and reports:

- the sampled `int32_t` token ID;
- an integer EOG flag determined by `llama_vocab_is_eog`.

Sampling does not call `llama_decode`, change `token_count`, append KV state, or convert the token to text. Python exposes the result as immutable `NativeSample(token_id, is_eog)` and blocks sampling before prefill or a second sampling attempt.

The pinned llama.cpp simple example uses the same decode-then-`llama_sampler_sample(..., -1)` sequence; BabyAI keeps it behind its own stable ABI and uses greedy sampling first so this inference milestone is deterministic.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
