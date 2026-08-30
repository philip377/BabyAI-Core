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

#define BABYAI_NATIVE_ABI_VERSION 6u

typedef struct babyai_native_runtime babyai_native_runtime;
typedef struct babyai_native_model babyai_native_model;
typedef struct babyai_native_context babyai_native_context;

typedef enum babyai_native_result {
    BABYAI_NATIVE_OK = 0,
    BABYAI_NATIVE_INVALID_ARGUMENT = 1,
    BABYAI_NATIVE_OUT_OF_MEMORY = 2,
    BABYAI_NATIVE_MODEL_LOAD_FAILED = 3,
    BABYAI_NATIVE_INTERNAL_ERROR = 4,
    BABYAI_NATIVE_CONTEXT_CREATE_FAILED = 5,
    BABYAI_NATIVE_BUFFER_TOO_SMALL = 6,
    BABYAI_NATIVE_TOKENIZE_FAILED = 7,
    BABYAI_NATIVE_PREFILL_TOO_LARGE = 8,
    BABYAI_NATIVE_CONTEXT_NOT_EMPTY = 9,
    BABYAI_NATIVE_DECODE_FAILED = 10,
    BABYAI_NATIVE_SAMPLE_NOT_READY = 11,
    BABYAI_NATIVE_SAMPLE_ALREADY_TAKEN = 12,
    BABYAI_NATIVE_SAMPLER_FAILED = 13,
    BABYAI_NATIVE_DECODE_NOT_READY = 14,
    BABYAI_NATIVE_DECODE_TOKEN_MISMATCH = 15,
    BABYAI_NATIVE_CONTEXT_FULL = 16,
    BABYAI_NATIVE_TOKEN_TO_PIECE_FAILED = 17,
    BABYAI_NATIVE_CONTEXT_UNUSABLE = 18,
} babyai_native_result;

BABYAI_NATIVE_API uint32_t babyai_native_abi_version(void);

// Optional ABI v6 extension. Returns a process-lifetime static UTF-8 literal
// describing the backend compiled into this DLL (currently "cpu" or "vulkan").
BABYAI_NATIVE_API const char * babyai_native_build_backend(void);

BABYAI_NATIVE_API int32_t babyai_native_runtime_create(
    babyai_native_runtime ** out_runtime);

BABYAI_NATIVE_API void babyai_native_runtime_destroy(
    babyai_native_runtime * runtime);

// Optional ABI v6 extension. Returns 1 only when the initialized llama.cpp
// backend registry exposes a real GPU/iGPU offload device, otherwise 0.
BABYAI_NATIVE_API int32_t babyai_native_runtime_gpu_available(
    const babyai_native_runtime * runtime);

BABYAI_NATIVE_API int32_t babyai_native_model_open(
    babyai_native_runtime * runtime,
    const char * model_path_utf8,
    int32_t n_gpu_layers,
    babyai_native_model ** out_model);

BABYAI_NATIVE_API void babyai_native_model_close(
    babyai_native_model * model);

// Optional ABI v6 extension. Returns the GGUF general.architecture value for
// the lifetime of the open model, or null when the metadata is unavailable.
BABYAI_NATIVE_API const char * babyai_native_model_architecture(
    const babyai_native_model * model);

BABYAI_NATIVE_API int32_t babyai_native_model_tokenize(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    const char * text_utf8,
    int32_t text_len,
    int32_t add_special,
    int32_t parse_special,
    int32_t * tokens_out,
    int32_t token_capacity,
    int32_t * out_token_count);

BABYAI_NATIVE_API int32_t babyai_native_model_token_to_piece(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    int32_t token,
    int32_t render_special,
    char * piece_out,
    int32_t piece_capacity,
    int32_t * out_piece_len);

BABYAI_NATIVE_API int32_t babyai_native_context_create(
    babyai_native_runtime * runtime,
    babyai_native_model * model,
    uint32_t n_ctx,
    uint32_t n_batch,
    int32_t n_threads,
    babyai_native_context ** out_context);

BABYAI_NATIVE_API void babyai_native_context_destroy(
    babyai_native_context * context);

BABYAI_NATIVE_API uint32_t babyai_native_context_n_ctx(
    const babyai_native_context * context);

BABYAI_NATIVE_API uint32_t babyai_native_context_n_batch(
    const babyai_native_context * context);

BABYAI_NATIVE_API int32_t babyai_native_context_prefill(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    const int32_t * tokens,
    int32_t token_count);

BABYAI_NATIVE_API uint32_t babyai_native_context_token_count(
    const babyai_native_context * context);

BABYAI_NATIVE_API int32_t babyai_native_context_sample_greedy(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    int32_t * out_token,
    int32_t * out_is_eog);

BABYAI_NATIVE_API int32_t babyai_native_context_decode_sampled(
    babyai_native_runtime * runtime,
    babyai_native_context * context,
    int32_t token);

BABYAI_NATIVE_API const char * babyai_native_last_error(
    const babyai_native_runtime * runtime);

#ifdef __cplusplus
}
#endif
