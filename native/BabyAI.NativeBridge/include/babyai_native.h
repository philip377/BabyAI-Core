#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  if defined(BABYAI_NATIVE_BUILD)
#    define BABYAI_NATIVE_API __declspec(dllexport)
#  else
#    define BABYAI_NATIVE_API __declspec(dllimport)
#  endif
#else
#  define BABYAI_NATIVE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define BABYAI_NATIVE_ABI_VERSION 1u

typedef struct babyai_native_runtime babyai_native_runtime;
typedef struct babyai_native_model babyai_native_model;

typedef enum babyai_native_result {
    BABYAI_NATIVE_OK = 0,
    BABYAI_NATIVE_INVALID_ARGUMENT = 1,
    BABYAI_NATIVE_OUT_OF_MEMORY = 2,
    BABYAI_NATIVE_MODEL_LOAD_FAILED = 3,
    BABYAI_NATIVE_INTERNAL_ERROR = 4,
} babyai_native_result;

BABYAI_NATIVE_API uint32_t babyai_native_abi_version(void);

BABYAI_NATIVE_API int32_t babyai_native_runtime_create(
    babyai_native_runtime ** out_runtime);

BABYAI_NATIVE_API void babyai_native_runtime_destroy(
    babyai_native_runtime * runtime);

BABYAI_NATIVE_API int32_t babyai_native_model_open(
    babyai_native_runtime * runtime,
    const char * model_path_utf8,
    int32_t n_gpu_layers,
    babyai_native_model ** out_model);

BABYAI_NATIVE_API void babyai_native_model_close(
    babyai_native_model * model);

// Pointer remains valid until the next operation on this runtime or runtime destroy.
BABYAI_NATIVE_API const char * babyai_native_last_error(
    const babyai_native_runtime * runtime);

#ifdef __cplusplus
}
#endif
