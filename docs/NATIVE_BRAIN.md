# Native Brain roadmap

BabyAI currently supports `ollama`, `echo`, and the reserved `native` provider.

The native path is intentionally incremental:

1. **Provider boundary** — Core constructs every brain through one factory.
2. **GGUF + runtime configuration** — `BABYAI_NATIVE_MODEL` points to a local GGUF file and `BABYAI_NATIVE_RUNTIME` points to the local llama.cpp shared library. Defaults live under `~/.babyai/models` and `~/.babyai/runtime`.
3. **Runtime loader** — `NativeRuntimeLoader` explicitly loads the shared library and verifies BabyAI's minimum llama.cpp ABI. Status/readiness checks do not dynamically load the library.
4. **Model lifecycle** — next: initialize llama.cpp, load the configured GGUF model, create/free a context, and expose deterministic smoke generation.
5. **Generation** — tokenization, decode loop, sampling, cancellation, context limits, and error translation.
6. **Packaging** — ship the tested runtime next to BabyAI so Ollama becomes optional rather than required.

## Safety and compatibility boundaries

- Native mode never downloads a model or runtime automatically.
- Native mode never shells out to `llama-cli` or `llama-server`.
- Dynamic library loading happens only through an explicit native-runtime call, not during ordinary status polling.
- Ollama remains the default until native generation passes Core and Windows Desktop CI plus manual Windows smoke testing.
- Core permissions, MEMORIA, identity, and learning semantics are independent of the selected inference backend.

## llama.cpp ABI v1

The first loader contract verifies the public lifecycle symbols BabyAI will need for the next model-loading step:

- `llama_backend_init` / `llama_backend_free`
- `llama_model_default_params`
- `llama_context_default_params`
- `llama_model_load_from_file` / `llama_model_free`
- `llama_init_from_model` / `llama_free`

No function is invoked by the loader contract yet; the loader only resolves the library and verifies symbol presence.
