cmake_minimum_required(VERSION 3.20)

project(BabyAI.NativeBridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(BABYAI_LLAMA_CPP_DIR "" CACHE PATH "Path to a pinned llama.cpp checkout")
if (NOT BABYAI_LLAMA_CPP_DIR)
    message(FATAL_ERROR "BABYAI_LLAMA_CPP_DIR must point to a llama.cpp checkout")
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
)

target_compile_definitions(babyai_native PRIVATE BABYAI_NATIVE_BUILD)
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
