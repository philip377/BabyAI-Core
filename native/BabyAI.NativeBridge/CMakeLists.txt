cmake_minimum_required(VERSION 3.20)

project(BabyAI.NativeBridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(BABYAI_LLAMA_CPP_DIR "" CACHE PATH "Path to a pinned llama.cpp checkout")
if (NOT BABYAI_LLAMA_CPP_DIR)
    message(FATAL_ERROR "BABYAI_LLAMA_CPP_DIR must point to a llama.cpp checkout")
endif()

set(BABYAI_NATIVE_BACKEND "cpu" CACHE STRING "BabyAI native backend: cpu or vulkan")
set_property(CACHE BABYAI_NATIVE_BACKEND PROPERTY STRINGS cpu vulkan)
string(TOLOWER "${BABYAI_NATIVE_BACKEND}" BABYAI_NATIVE_BACKEND_NORMALIZED)

if (BABYAI_NATIVE_BACKEND_NORMALIZED STREQUAL "vulkan")
    set(GGML_VULKAN ON CACHE BOOL "" FORCE)
elseif (BABYAI_NATIVE_BACKEND_NORMALIZED STREQUAL "cpu")
    set(GGML_VULKAN OFF CACHE BOOL "" FORCE)
else()
    message(FATAL_ERROR "BABYAI_NATIVE_BACKEND must be either cpu or vulkan")
endif()

# BabyAI owns the public DLL boundary. Keep llama.cpp/ggml static inside it so
# callers only need babyai_native.dll and never bind upstream structs directly.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_STATIC ON CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_APP OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_UI OFF CACHE BOOL "" FORCE)

add_subdirectory("${BABYAI_LLAMA_CPP_DIR}" "${CMAKE_BINARY_DIR}/llama.cpp" EXCLUDE_FROM_ALL)

add_library(babyai_native SHARED
    src/babyai_native.cpp
    src/babyai_native_backend.cpp
)

target_compile_definitions(babyai_native PRIVATE BABYAI_NATIVE_BUILD)
if (BABYAI_NATIVE_BACKEND_NORMALIZED STREQUAL "vulkan")
    target_compile_definitions(babyai_native PRIVATE BABYAI_NATIVE_BACKEND_VULKAN=1)
else()
    target_compile_definitions(babyai_native PRIVATE BABYAI_NATIVE_BACKEND_CPU=1)
endif()

target_include_directories(babyai_native
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${BABYAI_LLAMA_CPP_DIR}/include"
)
target_link_libraries(babyai_native PRIVATE llama)

if (MSVC)
    target_compile_options(babyai_native PRIVATE /W4 /permissive-)
else()
    target_compile_options(babyai_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(babyai_native PROPERTIES
    OUTPUT_NAME "babyai_native"
)
