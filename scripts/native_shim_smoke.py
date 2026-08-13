from __future__ import annotations

import ctypes
import sys
from pathlib import Path


BABYAI_NATIVE_OK = 0
BABYAI_NATIVE_MODEL_LOAD_FAILED = 3


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: native_shim_smoke.py <babyai_native library>", file=sys.stderr)
        return 2

    library_path = Path(sys.argv[1]).resolve()
    if not library_path.is_file():
        print(f"native shim not found: {library_path}", file=sys.stderr)
        return 2

    library = ctypes.CDLL(str(library_path))

    library.babyai_native_abi_version.restype = ctypes.c_uint32
    abi = int(library.babyai_native_abi_version())
    if abi != 1:
        raise RuntimeError(f"unexpected BabyAI native ABI: {abi}")

    library.babyai_native_runtime_create.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
    library.babyai_native_runtime_create.restype = ctypes.c_int32
    library.babyai_native_runtime_destroy.argtypes = [ctypes.c_void_p]
    library.babyai_native_runtime_destroy.restype = None
    library.babyai_native_model_open.argtypes = [
        ctypes.c_void_p,
        ctypes.c_char_p,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    library.babyai_native_model_open.restype = ctypes.c_int32
    library.babyai_native_last_error.argtypes = [ctypes.c_void_p]
    library.babyai_native_last_error.restype = ctypes.c_char_p

    runtime = ctypes.c_void_p()
    result = int(library.babyai_native_runtime_create(ctypes.byref(runtime)))
    if result != BABYAI_NATIVE_OK or not runtime.value:
        raise RuntimeError(f"native runtime create failed with code {result}")

    try:
        model = ctypes.c_void_p()
        missing_model = str(library_path.parent / "definitely-missing-babyai-model.gguf").encode("utf-8")
        result = int(
            library.babyai_native_model_open(
                runtime,
                missing_model,
                0,
                ctypes.byref(model),
            )
        )
        if result != BABYAI_NATIVE_MODEL_LOAD_FAILED:
            raise RuntimeError(f"missing-model smoke returned unexpected code {result}")
        if model.value:
            raise RuntimeError("missing-model smoke unexpectedly returned a model handle")

        raw_error = library.babyai_native_last_error(runtime)
        error = raw_error.decode("utf-8", errors="replace") if raw_error else ""
        if "GGUF" not in error:
            raise RuntimeError(f"native shim did not expose the expected model error: {error!r}")
    finally:
        library.babyai_native_runtime_destroy(runtime)

    print(f"BabyAI native shim smoke OK: abi={abi}; path={library_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
