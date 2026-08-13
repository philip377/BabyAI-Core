# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the in-process `native` GGUF provider.

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
11. **Bounded generation** — managed generation iterates sample/piece/decode with token, context and output bounds, cooperative cancellation, EOG handling, and combined UTF-8 decoding.
12. **Provider integration** — `NativeBrainProvider.generate()` now owns an explicit runtime/model lifetime and exposes bounded GGUF generation through the normal `LLMProvider` path.
13. **Windows GGUF smoke + packaging** — next: ship/test a compatible model and shim together, tune runtime settings, and verify real Orb chat on Windows before considering native the default.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit generation/runtime call, not ordinary status polling.
- The shim is the only native ABI BabyAI Core binds to; llama.cpp structs stay inside C++.
- llama.cpp is pinned in CI; upgrades are focused compatibility changes.
- Native/managed ownership releases contexts before models and models before the backend.
- `NativeBrainProvider` currently opens a fresh runtime/model lifetime for each `generate()` call so cleanup is explicit and command failures cannot retain uncertain native state.
- Token buffers are caller-owned and Python caps tokenization at 1,000,000 tokens before allocation.
- Token-piece buffers are caller-owned and Python caps one piece at 1 MiB before allocation.
- Prompt prefill is one-shot and must fit both actual `n_ctx` and actual `n_batch`.
- Any non-zero prefill or append decode result makes the context non-retriable; callers create a fresh context.
- Greedy sampling is deterministic and produces one pending token. Another sample is rejected until that exact token is appended.
- Append decode never accepts an arbitrary replacement token and never writes beyond the actual native context size.
- Managed generation stops before sampling when no append position remains, so it never creates a token it cannot commit for continued generation.
- Managed generation caps one request at 4,096 generated tokens and requires an explicit positive output-byte limit.
- Cancellation is cooperative at token boundaries; it does not interrupt a native `llama_decode` already in progress.
- Token pieces remain raw bytes until the generation layer combines them. This preserves UTF-8 code points split across tokens.
- If a caller-imposed stop lands on a partial UTF-8 code point, only the incomplete suffix is omitted. EOG-complete output must be valid UTF-8 or generation fails explicitly.
- Readiness remains read-only: it checks that the configured GGUF and BabyAI runtime files exist but does not load native code. ABI/model validation happens only when generation is explicitly requested.
- Ollama remains the default until native provider generation passes Core, Windows Desktop, Native Shim CI, and manual Windows GGUF smoke testing.
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

The managed `NativeModelHandle.token_to_piece()` returns `bytes` rather than eagerly decoding each piece as UTF-8. This preserves byte sequences when one Unicode code point is split across multiple tokens.

## Managed bounded generation

`babyai.native_generation.generate_greedy()` composes ABI v6 without adding another native ABI revision. It tokenizes the prompt, creates one bounded context, performs prefill, then repeats:

`sample -> token piece bytes -> append decode -> sample`

Generation stops on EOG, `max_tokens`, context capacity, output-byte capacity, or a cooperative cancellation check. The result records generated token count, raw output byte count and the stop reason alongside text.

Pieces are accumulated before UTF-8 decoding. At EOG the complete byte stream must decode strictly. At caller-imposed boundaries, an incomplete trailing code point can remain buffered and is omitted instead of emitting a replacement character.

## Native provider integration

`NativeBrainProvider.generate()` opens the configured BabyAI runtime, loads the configured GGUF model, calls bounded greedy generation, and closes model/runtime ownership before returning. Native runtime failures are translated into the existing `LLMError` boundary so CLI/Desktop callers get the same provider-level failure semantics as Ollama.

The first provider defaults are intentionally conservative and explicit: 256 generated tokens, 1 MiB output, `n_ctx=4096`, `n_batch=4096`, CPU-only model loading (`n_gpu_layers=0`), and llama.cpp default thread selection. Runtime tuning and persistent model residency are later performance milestones, not prerequisites for correctness.

The read-only readiness probe reports native as ready when both configured files exist. It deliberately does not load the DLL or model during status polling; actual ABI/model compatibility is checked by the explicit generation call.

The pinned llama.cpp simple example follows the same evaluate → sample → token-to-piece → next-token-decode sequence; BabyAI keeps each native operation behind its own stable ABI and the iteration policy in managed Core code.

## Pinned llama.cpp revision

Native Shim CI currently builds against:

`e79e4bf660e19f2ad851e06c6913f7a8c5852621`

Changing this revision should happen in a focused compatibility PR that rebuilds and smoke-tests the shim before merge.
