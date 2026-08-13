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
    std::vector<babyai_native_context *> contexts;
};

struct babyai_native_context {
    llama_context * handle = nullptr;
    babyai_native_model * model = nullptr;
};

namespace {

std::mutex g_backend_mutex;
std::size_t g_backend_ref_count = 0;

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
            if (params.n_ubatch > params.n_batch) {
                params.n_ubatch = params.n_batch;
            }
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
        wrapper->model = model;
        try {
            model->contexts.push_back(wrapper);
        } catch (...) {
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

const char * babyai_native_last_error(const babyai_native_runtime * runtime) {
    if (runtime == nullptr) {
        return "BabyAI native runtime handle is null.";
    }
    return runtime->last_error.c_str();
}

} // extern "C"
