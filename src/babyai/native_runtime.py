from __future__ import annotations

import ctypes
import os
from collections.abc import Sequence
from contextlib import nullcontext
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


BABYAI_NATIVE_ABI_VERSION = 5
BABYAI_NATIVE_OK = 0
BABYAI_NATIVE_BUFFER_TOO_SMALL = 6
MAX_NATIVE_TOKEN_COUNT = 1_000_000
MAX_NATIVE_TEXT_BYTES = 2_147_483_647
MAX_NATIVE_TOKEN_ID = 2_147_483_647


class NativeRuntimeError(RuntimeError):
    """Raised when the configured BabyAI native runtime cannot be used safely."""


@dataclass(frozen=True, slots=True)
class NativeSample:
    token_id: int
    is_eog: bool


REQUIRED_BABYAI_SYMBOLS: tuple[str, ...] = (
    "babyai_native_abi_version",
    "babyai_native_runtime_create",
    "babyai_native_runtime_destroy",
    "babyai_native_model_open",
    "babyai_native_model_close",
    "babyai_native_model_tokenize",
    "babyai_native_context_create",
    "babyai_native_context_destroy",
    "babyai_native_context_n_ctx",
    "babyai_native_context_n_batch",
    "babyai_native_context_prefill",
    "babyai_native_context_token_count",
    "babyai_native_context_sample_greedy",
    "babyai_native_last_error",
)


def _configure_abi(library: Any) -> None:
    library.babyai_native_abi_version.argtypes = []
    library.babyai_native_abi_version.restype = ctypes.c_uint32

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
    library.babyai_native_model_close.argtypes = [ctypes.c_void_p]
    library.babyai_native_model_close.restype = None
    library.babyai_native_model_tokenize.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_char_p,
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_int32),
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_int32),
    ]
    library.babyai_native_model_tokenize.restype = ctypes.c_int32

    library.babyai_native_context_create.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    library.babyai_native_context_create.restype = ctypes.c_int32
    library.babyai_native_context_destroy.argtypes = [ctypes.c_void_p]
    library.babyai_native_context_destroy.restype = None
    library.babyai_native_context_n_ctx.argtypes = [ctypes.c_void_p]
    library.babyai_native_context_n_ctx.restype = ctypes.c_uint32
    library.babyai_native_context_n_batch.argtypes = [ctypes.c_void_p]
    library.babyai_native_context_n_batch.restype = ctypes.c_uint32
    library.babyai_native_context_prefill.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_int32),
        ctypes.c_int32,
    ]
    library.babyai_native_context_prefill.restype = ctypes.c_int32
    library.babyai_native_context_token_count.argtypes = [ctypes.c_void_p]
    library.babyai_native_context_token_count.restype = ctypes.c_uint32
    library.babyai_native_context_sample_greedy.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_int32),
        ctypes.POINTER(ctypes.c_int32),
    ]
    library.babyai_native_context_sample_greedy.restype = ctypes.c_int32

    library.babyai_native_last_error.argtypes = [ctypes.c_void_p]
    library.babyai_native_last_error.restype = ctypes.c_char_p


def _last_error(library: Any, runtime_pointer: ctypes.c_void_p) -> str:
    if not runtime_pointer.value:
        return ""
    raw = library.babyai_native_last_error(runtime_pointer)
    if not raw:
        return ""
    if isinstance(raw, bytes):
        return raw.decode("utf-8", errors="replace").strip()
    return str(raw).strip()


@dataclass(slots=True)
class NativeContextHandle:
    """Managed opaque llama context owned by one native model handle."""

    model: NativeModelHandle
    pointer: ctypes.c_void_p
    context_size: int
    batch_size: int
    token_count: int = 0
    _sample_taken: bool = False
    _closed: bool = False

    @property
    def closed(self) -> bool:
        return self._closed

    def prefill(self, tokens: Sequence[int]) -> int:
        """Decode one initial bounded prompt into a fresh native context."""

        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native context handle is closed.")
        if isinstance(tokens, (str, bytes, bytearray)) or not isinstance(tokens, Sequence):
            raise NativeRuntimeError("Native prefill tokens must be a sequence of integer token IDs.")

        count = len(tokens)
        if count <= 0:
            raise NativeRuntimeError("Native prefill requires at least one token.")
        if count > MAX_NATIVE_TOKEN_COUNT:
            raise NativeRuntimeError(
                f"Native prefill has {count} tokens, exceeding the safety limit of {MAX_NATIVE_TOKEN_COUNT}."
            )
        if count > self.context_size or count > self.batch_size:
            raise NativeRuntimeError(
                f"Native prefill has {count} tokens but this context allows at most "
                f"{min(self.context_size, self.batch_size)} in the initial batch."
            )

        normalized: list[int] = []
        for token in tokens:
            if isinstance(token, bool) or not isinstance(token, int):
                raise NativeRuntimeError("Native prefill token IDs must be integers.")
            if token < 0 or token > MAX_NATIVE_TOKEN_ID:
                raise NativeRuntimeError(f"Native token ID {token} is outside the supported int32 range.")
            normalized.append(token)

        buffer_type = ctypes.c_int32 * count
        buffer = buffer_type(*normalized)
        library = self.model.runtime.handle.library
        result = int(
            library.babyai_native_context_prefill(
                self.model.runtime.pointer,
                self.pointer,
                buffer,
                count,
            )
        )
        if result != BABYAI_NATIVE_OK:
            detail = _last_error(library, self.model.runtime.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(f"Could not prefill native context (code {result}){suffix}")

        actual = int(library.babyai_native_context_token_count(self.pointer))
        if actual != count:
            raise NativeRuntimeError(
                f"Native prefill reported {actual} committed tokens after decoding {count}."
            )
        self.token_count = actual
        return actual

    def sample_greedy(self) -> NativeSample:
        """Select exactly one deterministic next token from current prefill logits."""

        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native context handle is closed.")
        if self.token_count <= 0:
            raise NativeRuntimeError("Native context must be prefilled before sampling.")
        if self._sample_taken:
            raise NativeRuntimeError("Native context already sampled the current logits.")

        library = self.model.runtime.handle.library
        out_token = ctypes.c_int32(-1)
        out_is_eog = ctypes.c_int32(0)
        result = int(
            library.babyai_native_context_sample_greedy(
                self.model.runtime.pointer,
                self.pointer,
                ctypes.byref(out_token),
                ctypes.byref(out_is_eog),
            )
        )
        if result != BABYAI_NATIVE_OK:
            detail = _last_error(library, self.model.runtime.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(f"Could not sample native next token (code {result}){suffix}")

        token_id = int(out_token.value)
        if token_id < 0 or token_id > MAX_NATIVE_TOKEN_ID:
            raise NativeRuntimeError(f"Native sampler returned invalid token ID {token_id}.")
        if out_is_eog.value not in (0, 1):
            raise NativeRuntimeError(f"Native sampler returned invalid EOG flag {out_is_eog.value}.")

        self._sample_taken = True
        return NativeSample(token_id=token_id, is_eog=bool(out_is_eog.value))

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        try:
            if self.pointer.value:
                self.model.runtime.handle.library.babyai_native_context_destroy(self.pointer)
        finally:
            self.pointer = ctypes.c_void_p()
            self.model._forget_context(self)

    def __enter__(self) -> NativeContextHandle:
        if self._closed:
            raise NativeRuntimeError("Native context handle is already closed.")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


@dataclass(slots=True)
class NativeModelHandle:
    """Managed opaque GGUF model handle owned by one native runtime session."""

    runtime: NativeRuntimeSession
    path: Path
    pointer: ctypes.c_void_p
    _contexts: list[NativeContextHandle] = field(default_factory=list)
    _closed: bool = False

    @property
    def closed(self) -> bool:
        return self._closed

    def tokenize(
        self,
        text: str,
        *,
        add_special: bool = True,
        parse_special: bool = False,
    ) -> list[int]:
        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native model handle is closed.")
        if not isinstance(text, str):
            raise NativeRuntimeError("Native tokenization text must be a string.")

        encoded = text.encode("utf-8")
        if len(encoded) > MAX_NATIVE_TEXT_BYTES:
            raise NativeRuntimeError("Native tokenization input exceeds the int32 byte limit.")

        library = self.runtime.handle.library
        required_count = ctypes.c_int32()
        result = int(
            library.babyai_native_model_tokenize(
                self.runtime.pointer,
                self.pointer,
                encoded,
                len(encoded),
                int(bool(add_special)),
                int(bool(parse_special)),
                None,
                0,
                ctypes.byref(required_count),
            )
        )
        if result not in (BABYAI_NATIVE_OK, BABYAI_NATIVE_BUFFER_TOO_SMALL):
            detail = _last_error(library, self.runtime.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(f"Could not size native tokenization (code {result}){suffix}")

        required = int(required_count.value)
        if required < 0:
            raise NativeRuntimeError("Native tokenization returned a negative token count.")
        if required > MAX_NATIVE_TOKEN_COUNT:
            raise NativeRuntimeError(
                f"Native tokenization requires {required} tokens, exceeding the safety limit of "
                f"{MAX_NATIVE_TOKEN_COUNT}."
            )
        if required == 0:
            return []

        buffer_type = ctypes.c_int32 * required
        buffer = buffer_type()
        actual_count = ctypes.c_int32()
        result = int(
            library.babyai_native_model_tokenize(
                self.runtime.pointer,
                self.pointer,
                encoded,
                len(encoded),
                int(bool(add_special)),
                int(bool(parse_special)),
                buffer,
                required,
                ctypes.byref(actual_count),
            )
        )
        if result != BABYAI_NATIVE_OK:
            detail = _last_error(library, self.runtime.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(f"Could not tokenize native text (code {result}){suffix}")

        actual = int(actual_count.value)
        if actual < 0 or actual > required:
            raise NativeRuntimeError(
                f"Native tokenization returned invalid token count {actual}; allocated {required}."
            )
        return [int(buffer[index]) for index in range(actual)]

    def open_context(
        self,
        *,
        n_ctx: int = 0,
        n_batch: int = 0,
        n_threads: int = 0,
    ) -> NativeContextHandle:
        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native model handle is closed.")
        if n_ctx < 0 or n_batch < 0 or n_threads < 0:
            raise NativeRuntimeError("Native context parameters must be non-negative.")

        library = self.runtime.handle.library
        out_context = ctypes.c_void_p()
        result = int(
            library.babyai_native_context_create(
                self.runtime.pointer,
                self.pointer,
                int(n_ctx),
                int(n_batch),
                int(n_threads),
                ctypes.byref(out_context),
            )
        )
        if result != BABYAI_NATIVE_OK or not out_context.value:
            detail = _last_error(library, self.runtime.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(f"Could not create native model context (code {result}){suffix}")

        context = NativeContextHandle(
            model=self,
            pointer=out_context,
            context_size=int(library.babyai_native_context_n_ctx(out_context)),
            batch_size=int(library.babyai_native_context_n_batch(out_context)),
            token_count=int(library.babyai_native_context_token_count(out_context)),
        )
        self._contexts.append(context)
        return context

    def _forget_context(self, context: NativeContextHandle) -> None:
        try:
            self._contexts.remove(context)
        except ValueError:
            pass

    def close(self) -> None:
        if self._closed:
            return

        for context in list(reversed(self._contexts)):
            context.close()

        self._closed = True
        try:
            if self.pointer.value:
                self.runtime.handle.library.babyai_native_model_close(self.pointer)
        finally:
            self.pointer = ctypes.c_void_p()
            self.runtime._forget_model(self)

    def __enter__(self) -> NativeModelHandle:
        if self._closed:
            raise NativeRuntimeError("Native model handle is already closed.")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


@dataclass(slots=True)
class NativeRuntimeSession:
    """Managed backend session that closes contexts, models, then the backend."""

    handle: NativeRuntimeHandle
    pointer: ctypes.c_void_p
    _models: list[NativeModelHandle] = field(default_factory=list)
    _closed: bool = False

    @property
    def closed(self) -> bool:
        return self._closed

    def open_model(self, model_path: Path, *, n_gpu_layers: int = 0) -> NativeModelHandle:
        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native runtime session is closed.")

        path = model_path.expanduser().resolve()
        out_model = ctypes.c_void_p()
        result = int(
            self.handle.library.babyai_native_model_open(
                self.pointer,
                str(path).encode("utf-8"),
                int(n_gpu_layers),
                ctypes.byref(out_model),
            )
        )
        if result != BABYAI_NATIVE_OK or not out_model.value:
            detail = _last_error(self.handle.library, self.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(
                f"Could not open native GGUF model '{path}' (code {result}){suffix}"
            )

        model = NativeModelHandle(runtime=self, path=path, pointer=out_model)
        self._models.append(model)
        return model

    def _forget_model(self, model: NativeModelHandle) -> None:
        try:
            self._models.remove(model)
        except ValueError:
            pass

    def close(self) -> None:
        if self._closed:
            return

        for model in list(reversed(self._models)):
            model.close()

        self._closed = True
        if self.pointer.value:
            self.handle.library.babyai_native_runtime_destroy(self.pointer)
        self.pointer = ctypes.c_void_p()

    def __enter__(self) -> NativeRuntimeSession:
        if self._closed:
            raise NativeRuntimeError("Native runtime session is already closed.")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


@dataclass(slots=True)
class NativeRuntimeHandle:
    path: Path
    library: Any
    abi_version: int

    def open_runtime(self) -> NativeRuntimeSession:
        out_runtime = ctypes.c_void_p()
        result = int(self.library.babyai_native_runtime_create(ctypes.byref(out_runtime)))
        if result != BABYAI_NATIVE_OK or not out_runtime.value:
            raise NativeRuntimeError(f"Could not create BabyAI native runtime (code {result}).")
        return NativeRuntimeSession(handle=self, pointer=out_runtime)


@dataclass(slots=True)
class NativeRuntimeLoader:
    """Explicitly load BabyAI's stable native shim and validate its ABI."""

    path: Path

    def load(self) -> NativeRuntimeHandle:
        path = self.path.expanduser().resolve()
        if not path.is_file():
            raise NativeRuntimeError(f"Native runtime library not found: {path}")

        dll_directory = (
            os.add_dll_directory(str(path.parent))
            if os.name == "nt" and hasattr(os, "add_dll_directory")
            else nullcontext()
        )
        try:
            with dll_directory:
                library = ctypes.CDLL(str(path))
        except OSError as exc:
            raise NativeRuntimeError(f"Could not load native runtime library '{path}': {exc}") from exc

        missing = [name for name in REQUIRED_BABYAI_SYMBOLS if not hasattr(library, name)]
        if missing:
            names = ", ".join(missing)
            raise NativeRuntimeError(
                f"Native runtime library '{path}' does not satisfy BabyAI native ABI v{BABYAI_NATIVE_ABI_VERSION}; "
                f"missing symbols: {names}"
            )

        _configure_abi(library)
        abi_version = int(library.babyai_native_abi_version())
        if abi_version != BABYAI_NATIVE_ABI_VERSION:
            raise NativeRuntimeError(
                f"Native runtime ABI mismatch: expected {BABYAI_NATIVE_ABI_VERSION}, got {abi_version}."
            )

        return NativeRuntimeHandle(path=path, library=library, abi_version=abi_version)

    def open_runtime(self) -> NativeRuntimeSession:
        return self.load().open_runtime()
