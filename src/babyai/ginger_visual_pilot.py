"""One-shot Ginger screen observation. No pointer/keyboard/UI writes.

The trusted desktop bridge may request a fresh, permissioned capture and ask
a local vision model to read it. This never links an item or creates a SKU.
"""
from __future__ import annotations

import ctypes
import os
from ctypes import wintypes
from dataclasses import dataclass
from typing import Any, Callable

from .ginger_local_vision import GingerVisualObservation, LocalGingerVision
from .ginger_supplier_matcher import normalize_text
from .screen_vision import ScreenCaptureStore


class GingerPilotError(ValueError):
    pass


def require_ginger_foreground() -> int:
    """Return foreground HWND only when its real executable is Ginger4.exe."""
    if os.name != "nt":
        raise GingerPilotError("Ginger visual pilot requires Windows")
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32.GetForegroundWindow.argtypes = []
    user32.GetForegroundWindow.restype = wintypes.HWND
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowThreadProcessId.restype = wintypes.DWORD
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    handle = user32.GetForegroundWindow()
    if not handle:
        raise GingerPilotError("No active Ginger window")
    pid = wintypes.DWORD(0)
    if not user32.GetWindowThreadProcessId(handle, ctypes.byref(pid)) or pid.value == 0:
        raise GingerPilotError("Could not identify active Windows process")
    proc = kernel32.OpenProcess(0x1000, False, pid.value)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not proc:
        raise GingerPilotError("Could not verify Ginger process executable")
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not kernel32.QueryFullProcessImageNameW(proc, 0, buffer, ctypes.byref(size)):
            raise GingerPilotError("Cannot verify Ginger executable path")
        name = buffer.value.replace("/", "\\").rsplit("\\", 1)[-1].casefold()
        if name != "ginger4.exe":
            raise GingerPilotError("Activate Ginger4.exe before starting visual inspection")
    finally:
        kernel32.CloseHandle(proc)
    return int(handle)


@dataclass(frozen=True, slots=True)
class GingerPilotObservation:
    capture_id: str
    supplier_title: str | None
    words: tuple[str, ...]
    candidates: tuple[str, ...]
    state: str
    next_step: str
    warnings: tuple[str, ...]
    read_only: bool = True

    def as_dict(self) -> dict[str, Any]:
        return {
            "capture_id": self.capture_id,
            "supplier_title": self.supplier_title,
            "starred_words": list(self.words),
            "catalogue_candidates": list(self.candidates),
            "state": self.state,
            "next_step": self.next_step,
            "warnings": list(self.warnings),
            "read_only": True,
            "ginger_updated": False,
        }


def interpret_ginger_frame(frame: GingerVisualObservation) -> GingerPilotObservation:
    """Interpret model text conservatively: never resolve SKU from pixels alone."""
    state = "pause"
    next_step = "inspect_screenshot_manually"
    warnings = list(frame.warnings)
    if frame.selected_supplier:
        # Hard guard: high-impact variants cannot be treated as their base
        # perfume just because the vision model says "possible_match".
        protected_variants = {"intense", "qahwa", "elixir", "extreme", "absolu"}
        source_tokens = set(normalize_text(frame.selected_supplier).split())
        source_variants = source_tokens & protected_variants
        candidate_tokens = [
            set(normalize_text(title).split())
            for title in frame.visible_catalog_candidates
        ]
        missing_variants = {
            variant for variant in source_variants
            if candidate_tokens and all(variant not in candidate for candidate in candidate_tokens)
        }
        if missing_variants:
            state, next_step = "template_discovery_only", "find_exact_flanker_or_propose_new_sku"
            warnings.append(
                "Different fragrance variant: " + ", ".join(sorted(missing_variants))
                + " is missing in all visible candidate titles"
            )
        elif frame.status == "possible_match" and frame.visible_catalog_candidates:
            state, next_step = "review_existing", "verify_exact_catalogue_identity"
        elif frame.status == "no_visible_candidates" and not frame.visible_catalog_candidates:
            state, next_step = "research_missing", "check_aliases_and_perfumery_sources"
        elif frame.status == "ambiguous":
            state, next_step = "review_ambiguity", "do_not_link_similar_variants"
    # A VLM is never allowed to write a product or approve a mapping.
    return GingerPilotObservation(
        capture_id=frame.observation_id,
        supplier_title=frame.selected_supplier,
        words=frame.starred_words,
        candidates=frame.visible_catalog_candidates,
        state=state,
        next_step=next_step,
        warnings=tuple(warnings),
    )


def observe_ginger_once(
    store: ScreenCaptureStore,
    *,
    port: int = 8080,
    foreground: Callable[[], int] = require_ginger_foreground,
    analyzer_factory: Callable[..., Any] = LocalGingerVision,
) -> GingerPilotObservation:
    """Capture Ginger's foreground window once, then ask a LOCAL VLM to read it.

    The same HWND must remain in focus throughout capture. The caller must
    already have SCREEN_CAPTURE permission; this function grants nothing.
    """
    before = foreground()
    capture = store.capture("active_window")
    after = foreground()
    if before != after:
        # Do not analyze a different app's captured content.
        store.delete(capture.id)
        raise GingerPilotError("Ginger window changed during screenshot capture")
    if capture.mode != "active_window":
        store.delete(capture.id)
        raise GingerPilotError("Ginger observation requires active-window capture")
    vision = analyzer_factory(store, port=port).analyze(capture.id)
    return interpret_ginger_frame(vision)
