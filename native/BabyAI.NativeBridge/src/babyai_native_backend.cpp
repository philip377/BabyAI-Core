#include "babyai_native.h"

extern "C" {

const char * babyai_native_build_backend(void) {
#if defined(BABYAI_NATIVE_BACKEND_VULKAN)
    return "vulkan";
#else
    return "cpu";
#endif
}

} // extern "C"
