#include "babyai_native.h"

#include "llama.h"

extern "C" {

const char * babyai_native_build_backend(void) {
#if defined(BABYAI_NATIVE_BACKEND_VULKAN)
    return "vulkan";
#else
    return "cpu";
#endif
}

int32_t babyai_native_runtime_gpu_available(const babyai_native_runtime * runtime) {
    if (runtime == nullptr) {
        return 0;
    }
    return llama_supports_gpu_offload() ? 1 : 0;
}

} // extern "C"
