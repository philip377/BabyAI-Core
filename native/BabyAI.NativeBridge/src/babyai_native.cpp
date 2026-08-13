#include "babyai_native.h"

#include "llama.h"

#include <cstdint>
#include <mutex>
#include <new>
#include <string>

struct babyai_native_runtime {
    std::string last_error;
};

struct babyai_native_model {
    llama_model * handle = nullptr;
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
    if (model->handle != nullptr) {
        llama_model_free(model->handle);
        model->handle = nullptr;
    }
    delete model;
}

const char * babyai_native_last_error(const babyai_native_runtime * runtime) {
    if (runtime == nullptr) {
        return "BabyAI native runtime handle is null.";
    }
    return runtime->last_error.c_str();
}

} // extern "C"
