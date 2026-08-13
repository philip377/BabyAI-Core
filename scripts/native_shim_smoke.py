from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from babyai.native_runtime import (  # noqa: E402
    BABYAI_NATIVE_ABI_VERSION,
    NativeRuntimeError,
    NativeRuntimeLoader,
)


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: native_shim_smoke.py <babyai_native library>", file=sys.stderr)
        return 2

    library_path = Path(sys.argv[1]).resolve()
    if not library_path.is_file():
        print(f"native shim not found: {library_path}", file=sys.stderr)
        return 2

    handle = NativeRuntimeLoader(library_path).load()
    if handle.abi_version != BABYAI_NATIVE_ABI_VERSION:
        raise RuntimeError(f"unexpected BabyAI native ABI: {handle.abi_version}")

    missing_model = library_path.parent / "definitely-missing-babyai-model.gguf"
    with handle.open_runtime() as runtime:
        try:
            runtime.open_model(missing_model, n_gpu_layers=0)
        except NativeRuntimeError as exc:
            message = str(exc)
            if "GGUF" not in message:
                raise RuntimeError(
                    f"managed native lifecycle did not expose the expected GGUF error: {message!r}"
                ) from exc
        else:
            raise RuntimeError("missing-model smoke unexpectedly returned a model handle")

    print(
        "BabyAI managed native lifecycle smoke OK: "
        f"abi={handle.abi_version}; path={library_path}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
