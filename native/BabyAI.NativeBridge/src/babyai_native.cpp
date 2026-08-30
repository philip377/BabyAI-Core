#include "babyai_native.h"

#include "llama.h"

#include <algorithm>
#include <cstdint>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <vector>

struct babyai_native_runtime {
    std::string last_error;
    std::vector<babyai_native_model *> models;
};

struct babyai_native_model {
    llama_model * handle = nullptr;
    babyai_native_runtime * runtime = nullptr;
    std::string architecture;
    std::vector<babyai_native_context *> contexts;
};

struct babyai_native_context {
    llama_context * handle = nullptr;
    llama_sampler * sampler = nullptr;
    babyai_native_model * model = nullptr;
    uint32_t token_count = 0;
    bool prefill_attempted = false;
    bool sample_taken = false;
    llama_token sampled_token = LLAMA_TOKEN_NULL;
    bool decode_failed = false;
};

namespace {

std::mutex g_backend_mutex;
std::size_t g_backend_ref_count = 0;

constexpr int32_t k_sampling_top_k = 64;
constexpr int32_t k_repeat_penalty_last_n = 64;
constexpr float k_repeat_penalty = 1.12f;

void backend_acquire() {
    std::scoped_lock lock(g_backend_mutex);
    if (g_backend_ref_count == 0) {
        llama_backend_init();
    }
    ++g_backend_ref_count;
}

void backend_release() {
    std::scoped_lock lock(g_backend_mutex);
    if (g_backend_ref_count == 0) {
        return;
    }
    --g_backend_ref_count;
    if (g_backend_ref_count == 0) {
        llama_backend_free();
    }
}

template <typename T>
void erase_pointer(std::vector<T *> & items, T * value) {
    items.erase(std::remove(items.begin(), items.end(), value), items.end());
}

int32_t fail(babyai_native_runtime * runtime, babyai_native_result code, const char * message) {
    if (runtime != nullptr) {
        runtime->last_error = message == nullptr ? "Unknown native error." : message;
    }
    return static_cast<int32_t>(code);
}

llama_sampler * create_generation_sampler(const llama_model * model) {
    if (model == nullptr) {
        return nullptr;
    }
    const llama_vocab * vocab = llama_model_get_vocab(model);
    if (vocab == nullptr) {
        return nullptr;
    }
    const int32_t vocab_size = llama_vocab_n_tokens(vocab);
    if (vocab_size <= 0) {
        return nullptr;
    }

    llama_sampler * chain = llama_sampler_chain_init(llama_sampler_chain_default_params());
    llama_sampler * top_k = llama_sampler_init_top_k(k_sampling_top_k);
    llama_sampler * penalties = llama_sampler_init_penalties(
        vocab_size,
        k_repeat_penalty_last_n,
        k_repeat_penalty,
        0.0f,
        0.0f);
    llama_sampler * greedy = llama_sampler_init_greedy();
    if (chain == nullptr || top_k == nullptr || penalties == nullptr || greedy == nullptr) {
        if (top_k != nullptr) {
            llama_sampler_free(top_k);
        }
        if (penalties != nullptr) {
            llama_sampler_free(penalties);
        }
        if (greedy != nullptr) {
            llama_sampler_free(greedy);
        }
        if (chain != nullptr) {
            llama_sampler_free(chain);
        }
        return nullptr;
    }

    // Keep the final choice deterministic while letting the persistent penalties
    // sampler remember recently generated tokens. Top-k bounds penalty work and
    // leaves ordinary greedy behavior unchanged when no repeated token is involved.
    llama_sampler_chain_add(chain, top_k);
    llama_sampler_chain_add(chain, penalties);
    llama_sampler_chain_add(chain, greedy);
    return chain;
}

} // namespace

extern "C" {

uint32_t babyai_native_abi_version(void) {
    return BABYAI_NATIVE_ABI_VERSION;
}

int32_t babyai_native_runtime_create(babyai_native_runtime ** out_runtime) {
    if (out_runtime == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_runtime = nullptr;

    try {
        auto * runtime = new (std::nothrow) babyai_native_runtime();
        if (runtime == nullptr) {
            return static_cast<int32_t>(BABYAI_NATIVE_OUT_OF_MEMORY);
        }

        try {
            backend_acquire();
        } catch (...) {
            delete runtime;
            return static_cast<int32_t>(BABYAI_NATIVE_INTERNAL_ERROR);
        }

        *out_runtime = runtime;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (...) {
        return static_cast<int32_t>(BABYAI_NATIVE_INTERNAL_ERROR);
    }
}

void babyai_native_runtime_destroy(babyai_native_runtime * runtime) {
    if (runtime == nullptr) {
        return;
    }

    while (!runtime->models.empty()) {
        babyai_native_model_close(runtime->models.back());
    }

    backend_release();
    delete runtime;
}

int32_t babyai_native_model_open(
    babyai_native_runtime * runtime,
    const char * model_path_utf8,
    int32_t n_gpu_layers,
    babyai_native_model ** out_model) {
    if (runtime == nullptr || model_path_utf8 == nullptr || out_model == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_model = nullptr;
    runtime->last_error.clear();

    try {
        llama_model_params params = llama_model_default_params();
        params.n_gpu_layers = n_gpu_layers;

        llama_model * model = llama_model_load_from_file(model_path_utf8, params);
        if (model == nullptr) {
            return fail(runtime, BABYAI_NATIVE_MODEL_LOAD_FAILED, "llama.cpp could not load the configured GGUF model.");
        }

        auto * wrapper = new (std::nothrow) babyai_native_model();
        if (wrapper == nullptr) {
            llama_model_free(model);
            return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not allocate the BabyAI native model handle.");
        }

        wrapper->handle = model;
        wrapper->runtime = runtime;
        const int32_t architecture_length = llama_model_meta_val_str(
            model,
            "general.architecture",
            nullptr,
            0);
        if (architecture_length > 0 && architecture_length <= 128) {
            char architecture[129] = {};
            const int32_t copied = llama_model_meta_val_str(
                model,
                "general.architecture",
                architecture,
                sizeof(architecture));
            if (copied == architecture_length) {
                try {
                    wrapper->architecture.assign(architecture, static_cast<std::size_t>(copied));
                } catch (const std::bad_alloc &) {
                    llama_model_free(model);
                    delete wrapper;
                    return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not store native model metadata.");
                }
            }
        }
        try {
            runtime->models.push_back(wrapper);
        } catch (...) {
            llama_model_free(model);
            delete wrapper;
            return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not register the BabyAI native model handle.");
        }

        *out_model = wrapper;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (...) {
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Unexpected native model lifecycle error.");
    }
}

const char * babyai_native_model_architecture(const babyai_native_model * model) {
    if (model == nullptr || model->handle == nullptr || model->architecture.empty()) {
        return nullptr;
    }
    return model->architecture.c_str();
}

void babyai_native_model_close(babyai_native_model * model) {
    if (model == nullptr) {
        return;
    }

    while (!model->contexts.empty()) {
        babyai_native_context_destroy(model->contexts.back());
    }

    if (model->handle != nullptr) {
        llama_model_free(model->handle);
        model->handle = nullptr;
    }

    if (model->runtime != nullptr) {
        erase_pointer(model->runtime->models, model);
        model->runtime = nullptr;
    }

    delete model;
}

int32_t babyai_native_model_tokenize(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    const char * text_utf8,
    int32_t text_len,
    int32_t add_special,
    int32_t parse_special,
    int32_t * tokens_out,
    int32_t token_capacity,
    int32_t * out_token_count) {
    if (runtime == nullptr || model == nullptr || text_utf8 == nullptr || out_token_count == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    if (text_len < 0 || token_capacity < 0 || (token_capacity > 0 && tokens_out == nullptr)) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_token_count = 0;
    runtime->last_error.clear();

    if (model->runtime != runtime || model->handle == nullptr) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native model does not belong to this runtime.");
    }

    try {
        const llama_vocab * vocab = llama_model_get_vocab(model->handle);
        if (vocab == nullptr) {
            return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Native model vocabulary is unavailable.");
        }

        const int32_t probe = llama_tokenize(
            vocab,
            text_utf8,
            text_len,
            nullptr,
            0,
            add_special != 0,
            parse_special != 0);

        if (probe == std::numeric_limits<int32_t>::min()) {
            return fail(runtime, BABYAI_NATIVE_TOKENIZE_FAILED, "llama.cpp tokenization size overflowed int32.");
        }

        const int32_t required = probe < 0 ? -probe : probe;
        *out_token_count = required;
        if (required == 0) {
            return static_cast<int32_t>(BABYAI_NATIVE_OK);
        }
        if (tokens_out == nullptr || token_capacity < required) {
            return static_cast<int32_t>(BABYAI_NATIVE_BUFFER_TOO_SMALL);
        }

        std::vector<llama_token> tokens(static_cast<std::size_t>(required));
        const int32_t actual = llama_tokenize(
            vocab,
            text_utf8,
            text_len,
            tokens.data(),
            required,
            add_special != 0,
            parse_special != 0);
        if (actual < 0 || actual > required) {
            return fail(runtime, BABYAI_NATIVE_TOKENIZE_FAILED, "llama.cpp could not tokenize the configured text.");
        }

        for (int32_t index = 0; index < actual; ++index) {
            tokens_out[index] = static_cast<int32_t>(tokens[static_cast<std::size_t>(index)]);
        }
        *out_token_count = actual;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (const std::bad_alloc &) {
        return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not allocate the native token buffer.");
    } catch (...) {
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Unexpected native tokenization error.");
    }
}

int32_t babyai_native_model_token_to_piece(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    int32_t token,
    int32_t render_special,
    char * piece_out,
    int32_t piece_capacity,
    int32_t * out_piece_len) {
    if (runtime == nullptr || model == nullptr || out_piece_len == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    if (token < 0 || piece_capacity < 0 || (piece_capacity > 0 && piece_out == nullptr)) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_piece_len = 0;
    runtime->last_error.clear();

    if (model->runtime != runtime || model->handle == nullptr) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native model does not belong to this runtime.");
    }

    try {
        const llama_vocab * vocab = llama_model_get_vocab(model->handle);
        if (vocab == nullptr) {
            return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Native model vocabulary is unavailable.");
        }
        const int32_t vocab_size = llama_vocab_n_tokens(vocab);
        if (token >= vocab_size) {
            return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native token ID is outside the model vocabulary.");
        }

        const llama_token native_token = static_cast<llama_token>(token);
        const int32_t probe = llama_token_to_piece(
            vocab,
            native_token,
            nullptr,
            0,
            0,
            render_special != 0);
        if (probe == std::numeric_limits<int32_t>::min()) {
            return fail(runtime, BABYAI_NATIVE_TOKEN_TO_PIECE_FAILED, "Native token piece size overflowed int32.");
        }

        const int32_t required = probe < 0 ? -probe : probe;
        *out_piece_len = required;
        if (required == 0) {
            return static_cast<int32_t>(BABYAI_NATIVE_OK);
        }
        if (piece_out == nullptr || piece_capacity < required) {
            return static_cast<int32_t>(BABYAI_NATIVE_BUFFER_TOO_SMALL);
        }

        const int32_t actual = llama_token_to_piece(
            vocab,
            native_token,
            piece_out,
            piece_capacity,
            0,
            render_special != 0);
        if (actual < 0 || actual > piece_capacity) {
            return fail(runtime, BABYAI_NATIVE_TOKEN_TO_PIECE_FAILED, "llama.cpp could not convert the token to a piece.");
        }
        *out_piece_len = actual;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (...) {
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Unexpected native token-to-piece error.");
    }
}

int32_t babyai_native_context_create(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    uint32_t n_ctx,
    uint32_t n_batch,
    int32_t n_threads,
    babyai_native_context ** out_context) {
    if (runtime == nullptr || model == nullptr || out_context == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_context = nullptr;
    runtime->last_error.clear();

    if (model->runtime != runtime || model->handle == nullptr) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native model does not belong to this runtime.");
    }

    try {
        llama_context_params params = llama_context_default_params();
        if (n_ctx > 0) {
            params.n_ctx = n_ctx;
        }
        if (n_batch > 0) {
            params.n_batch = n_batch;
            // Keep the public logical batch generous, but give llama.cpp enough
            // physical batch room for the typical BabyAI desktop prompt. The
            // upstream default is conservative and can split a ~600-token prompt
            // into multiple compute passes. 1024 covers that case without the
            // memory jump of matching the full 4096-token logical batch.
            constexpr uint32_t k_prefill_ubatch_cap = 1024;
            params.n_ubatch = std::min(params.n_batch, k_prefill_ubatch_cap);
        }
        if (n_threads > 0) {
            params.n_threads = n_threads;
            params.n_threads_batch = n_threads;
        }

        llama_context * context = llama_init_from_model(model->handle, params);
        if (context == nullptr) {
            return fail(runtime, BABYAI_NATIVE_CONTEXT_CREATE_FAILED, "llama.cpp could not create a context for the configured model.");
        }

        auto * wrapper = new (std::nothrow) babyai_native_context();
        if (wrapper == nullptr) {
            llama_free(context);
            return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not allocate the BabyAI native context handle.");
        }

        wrapper->handle = context;
        wrapper->sampler = create_generation_sampler(model->handle);
        wrapper->model = model;
        if (wrapper->sampler == nullptr) {
            llama_free(context);
            delete wrapper;
            return fail(runtime, BABYAI_NATIVE_SAMPLER_FAILED, "Could not create the native repetition-aware sampler.");
        }
        try {
            model->contexts.push_back(wrapper);
        } catch (...) {
            llama_sampler_free(wrapper->sampler);
            wrapper->sampler = nullptr;
            llama_free(context);
            delete wrapper;
            return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not register the BabyAI native context handle.");
        }

        *out_context = wrapper;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (...) {
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Unexpected native context lifecycle error.");
    }
}

void babyai_native_context_destroy(babyai_native_context * context) {
    if (context == nullptr) {
        return;
    }

    if (context->sampler != nullptr) {
        llama_sampler_free(context->sampler);
        context->sampler = nullptr;
    }

    if (context->handle != nullptr) {
        llama_free(context->handle);
        context->handle = nullptr;
    }

    if (context->model != nullptr) {
        erase_pointer(context->model->contexts, context);
        context->model = nullptr;
    }

    delete context;
}

uint32_t babyai_native_context_n_ctx(const babyai_native_context * context) {
    if (context == nullptr || context->handle == nullptr) {
        return 0;
    }
    return llama_n_ctx(context->handle);
}

uint32_t babyai_native_context_n_batch(const babyai_native_context * context) {
    if (context == nullptr || context->handle == nullptr) {
        return 0;
    }
    return llama_n_batch(context->handle);
}

int32_t babyai_native_context_prefill(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    const int32_t * tokens,
    int32_t token_count) {
    if (runtime == nullptr || context == nullptr || tokens == nullptr || token_count <= 0) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    runtime->last_error.clear();

    if (context->handle == nullptr || context->model == nullptr || context->model->runtime != runtime) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native context does not belong to this runtime.");
    }
    if (context->prefill_attempted || context->token_count != 0) {
        return fail(runtime, BABYAI_NATIVE_CONTEXT_NOT_EMPTY, "Native context already has a prefill attempt; create a fresh context.");
    }

    const uint32_t count = static_cast<uint32_t>(token_count);
    const uint32_t context_limit = llama_n_ctx(context->handle);
    const uint32_t batch_limit = llama_n_batch(context->handle);
    if (count > context_limit || count > batch_limit) {
        return fail(runtime, BABYAI_NATIVE_PREFILL_TOO_LARGE, "Prompt token count exceeds the current native context or batch limit.");
    }

    try {
        std::vector<llama_token> native_tokens(count);
        std::vector<llama_pos> positions(count);
        std::vector<int32_t> sequence_counts(count, 1);
        std::vector<llama_seq_id> sequence_storage(count, 0);
        std::vector<llama_seq_id *> sequence_ids(count);
        std::vector<int8_t> outputs(count, 0);

        for (uint32_t index = 0; index < count; ++index) {
            native_tokens[index] = static_cast<llama_token>(tokens[index]);
            positions[index] = static_cast<llama_pos>(index);
            sequence_ids[index] = &sequence_storage[index];
        }
        outputs[count - 1] = 1;

        llama_batch batch = {
            static_cast<int32_t>(count),
            native_tokens.data(),
            nullptr,
            positions.data(),
            sequence_counts.data(),
            sequence_ids.data(),
            outputs.data(),
        };

        context->prefill_attempted = true;
        const int32_t decode_result = llama_decode(context->handle, batch);
        if (decode_result != 0) {
            context->decode_failed = true;
            runtime->last_error =
                "llama.cpp prompt prefill failed with decode code " + std::to_string(decode_result) +
                "; create a fresh context before retrying.";
            return static_cast<int32_t>(BABYAI_NATIVE_DECODE_FAILED);
        }

        context->token_count = count;
        return static_cast<int32_t>(BABYAI_NATIVE_OK);
    } catch (const std::bad_alloc &) {
        return fail(runtime, BABYAI_NATIVE_OUT_OF_MEMORY, "Could not allocate the native prompt batch.");
    } catch (...) {
        context->prefill_attempted = true;
        context->decode_failed = true;
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Unexpected native prompt prefill error; create a fresh context.");
    }
}

uint32_t babyai_native_context_token_count(const babyai_native_context * context) {
    if (context == nullptr || context->handle == nullptr) {
        return 0;
    }
    return context->token_count;
}

int32_t babyai_native_context_sample_greedy(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    int32_t * out_token,
    int32_t * out_is_eog) {
    if (runtime == nullptr || context == nullptr || out_token == nullptr || out_is_eog == nullptr) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    *out_token = -1;
    *out_is_eog = 0;
    runtime->last_error.clear();

    if (context->handle == nullptr || context->model == nullptr || context->model->runtime != runtime) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native context does not belong to this runtime.");
    }
    if (context->decode_failed) {
        return fail(runtime, BABYAI_NATIVE_CONTEXT_UNUSABLE, "Native context is unusable after a decode failure; create a fresh context.");
    }
    if (!context->prefill_attempted || context->token_count == 0) {
        return fail(runtime, BABYAI_NATIVE_SAMPLE_NOT_READY, "Native context must complete prefill before sampling.");
    }
    if (context->sample_taken) {
        return fail(runtime, BABYAI_NATIVE_SAMPLE_ALREADY_TAKEN, "Native context already sampled the current logits; decode that token first.");
    }
    if (context->sampler == nullptr) {
        return fail(runtime, BABYAI_NATIVE_SAMPLER_FAILED, "Native repetition-aware sampler is unavailable.");
    }

    const llama_vocab * vocab = llama_model_get_vocab(context->model->handle);
    if (vocab == nullptr) {
        return fail(runtime, BABYAI_NATIVE_INTERNAL_ERROR, "Native model vocabulary is unavailable for sampling.");
    }

    const llama_token token = llama_sampler_sample(context->sampler, context->handle, -1);
    if (token == LLAMA_TOKEN_NULL) {
        return fail(runtime, BABYAI_NATIVE_SAMPLER_FAILED, "Native repetition-aware sampler returned LLAMA_TOKEN_NULL.");
    }

    *out_token = static_cast<int32_t>(token);
    *out_is_eog = llama_vocab_is_eog(vocab, token) ? 1 : 0;
    context->sample_taken = true;
    context->sampled_token = token;
    return static_cast<int32_t>(BABYAI_NATIVE_OK);
}

int32_t babyai_native_context_decode_sampled(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    int32_t token) {
    if (runtime == nullptr || context == nullptr || token < 0) {
        return static_cast<int32_t>(BABYAI_NATIVE_INVALID_ARGUMENT);
    }
    runtime->last_error.clear();

    if (context->handle == nullptr || context->model == nullptr || context->model->runtime != runtime) {
        return fail(runtime, BABYAI_NATIVE_INVALID_ARGUMENT, "Native context does not belong to this runtime.");
    }
    if (context->decode_failed) {
        return fail(runtime, BABYAI_NATIVE_CONTEXT_UNUSABLE, "Native context is unusable after a decode failure; create a fresh context.");
    }
    if (!context->sample_taken || context->sampled_token == LLAMA_TOKEN_NULL) {
        return fail(runtime, BABYAI_NATIVE_DECODE_NOT_READY, "Native context must sample a token before append decode.");
    }
    if (static_cast<llama_token>(token) != context->sampled_token) {
        return fail(runtime, BABYAI_NATIVE_DECODE_TOKEN_MISMATCH, "Append decode token does not match the pending sampled token.");
    }
    if (context->token_count >= llama_n_ctx(context->handle)) {
        return fail(runtime, BABYAI_NATIVE_CONTEXT_FULL, "Native context has no remaining token positions for append decode.");
    }

    llama_token native_token = context->sampled_token;
    llama_pos position = static_cast<llama_pos>(context->token_count);
    int32_t sequence_count = 1;
    llama_seq_id sequence_id = 0;
    llama_seq_id * sequence_ids = &sequence_id;
    int8_t output = 1;
    llama_batch batch = {
        1,
        &native_token,
        nullptr,
        &position,
        &sequence_count,
        &sequence_ids,
        &output,
    };

    const int32_t decode_result = llama_decode(context->handle, batch);
    if (decode_result != 0) {
        context->decode_failed = true;
        runtime->last_error =
            "llama.cpp sampled-token decode failed with decode code " + std::to_string(decode_result) +
            "; create a fresh context before retrying.";
        return static_cast<int32_t>(BABYAI_NATIVE_DECODE_FAILED);
    }

    ++context->token_count;
    context->sample_taken = false;
    context->sampled_token = LLAMA_TOKEN_NULL;
    return static_cast<int32_t>(BABYAI_NATIVE_OK);
}

const char * babyai_native_last_error(const babyai_native_runtime * runtime) {
    if (runtime == nullptr) {
        return "BabyAI native runtime handle is null.";
    }
    return runtime->last_error.c_str();
}

} // extern "C"
