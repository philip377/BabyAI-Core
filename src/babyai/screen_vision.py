from __future__ import annotations

import ctypes
import json
import os
import struct
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

from .permissions import Capability, PermissionStore


CAPTURE_MODES = frozenset({"active_window", "primary_screen"})
MAX_CAPTURE_PIXELS = 33_177_600  # 8K UHD


@dataclass(frozen=True, slots=True)
class ScreenObservation:
    id: str
    mode: str
    path: str
    width: int
    height: int
    captured_at: str
    window_title: str
    analysis_status: str = "capture_only"


def _capture_bmp(mode: str) -> tuple[bytes, int, int, str]:
    if os.name != "nt":
        raise OSError("Screen capture is available only on Windows")

    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32
    user32.GetForegroundWindow.restype = ctypes.c_void_p
    user32.GetDC.restype = ctypes.c_void_p
    gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
    gdi32.CreateCompatibleBitmap.restype = ctypes.c_void_p
    gdi32.SelectObject.restype = ctypes.c_void_p

    class Rect(ctypes.Structure):
        _fields_ = [
            ("left", ctypes.c_long),
            ("top", ctypes.c_long),
            ("right", ctypes.c_long),
            ("bottom", ctypes.c_long),
        ]

    user32.GetWindowRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(Rect)]
    user32.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]
    user32.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
    user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
    gdi32.CreateCompatibleBitmap.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    gdi32.BitBlt.argtypes = [
        ctypes.c_void_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_void_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_ulong,
    ]
    gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
    gdi32.DeleteDC.argtypes = [ctypes.c_void_p]

    title = "Primary screen"
    if mode == "active_window":
        hwnd = user32.GetForegroundWindow()
        if not hwnd:
            raise OSError("Windows did not report an active window")
        rect = Rect()
        if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            raise OSError("Could not read the active-window bounds")
        x, y = rect.left, rect.top
        width, height = rect.right - rect.left, rect.bottom - rect.top
        length = user32.GetWindowTextLengthW(hwnd)
        if length > 0:
            buffer = ctypes.create_unicode_buffer(min(length + 1, 1024))
            user32.GetWindowTextW(hwnd, buffer, len(buffer))
            title = buffer.value.strip() or "Active window"
    else:
        x, y = 0, 0
        width = user32.GetSystemMetrics(0)
        height = user32.GetSystemMetrics(1)

    if width <= 0 or height <= 0 or width * height > MAX_CAPTURE_PIXELS:
        raise ValueError(f"Refusing unsafe capture dimensions: {width}x{height}")

    screen_dc = user32.GetDC(0)
    memory_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    previous = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not gdi32.BitBlt(memory_dc, 0, 0, width, height, screen_dc, x, y, 0x40CC0020):
            raise OSError("Windows could not copy screen pixels")

        class BitmapInfoHeader(ctypes.Structure):
            _fields_ = [
                ("size", ctypes.c_uint32),
                ("width", ctypes.c_int32),
                ("height", ctypes.c_int32),
                ("planes", ctypes.c_uint16),
                ("bit_count", ctypes.c_uint16),
                ("compression", ctypes.c_uint32),
                ("size_image", ctypes.c_uint32),
                ("x_pixels_per_meter", ctypes.c_int32),
                ("y_pixels_per_meter", ctypes.c_int32),
                ("colors_used", ctypes.c_uint32),
                ("colors_important", ctypes.c_uint32),
            ]

        header = BitmapInfoHeader()
        header.size = ctypes.sizeof(BitmapInfoHeader)
        header.width = width
        header.height = -height  # top-down pixels
        header.planes = 1
        header.bit_count = 32
        pixel_bytes = width * height * 4
        header.size_image = pixel_bytes
        pixels = ctypes.create_string_buffer(pixel_bytes)
        gdi32.GetDIBits.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_uint,
            ctypes.c_uint,
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_uint,
        ]
        if not gdi32.GetDIBits(
            memory_dc,
            bitmap,
            0,
            height,
            pixels,
            ctypes.byref(header),
            0,
        ):
            raise OSError("Windows could not encode screen pixels")
        file_header = struct.pack("<2sIHHI", b"BM", 54 + pixel_bytes, 0, 0, 54)
        info_header = struct.pack(
            "<IiiHHIIiiII",
            40,
            width,
            -height,
            1,
            32,
            0,
            pixel_bytes,
            2835,
            2835,
            0,
            0,
        )
        return file_header + info_header + pixels.raw, width, height, title
    finally:
        if previous:
            gdi32.SelectObject(memory_dc, previous)
        if bitmap:
            gdi32.DeleteObject(bitmap)
        if memory_dc:
            gdi32.DeleteDC(memory_dc)
        if screen_dc:
            user32.ReleaseDC(0, screen_dc)


@dataclass(slots=True)
class ScreenCaptureStore:
    directory: Path
    permissions: PermissionStore

    @property
    def manifest_path(self) -> Path:
        return self.directory / "observations.json"

    def capture(self, mode: str) -> ScreenObservation:
        self.permissions.require(Capability.SCREEN_CAPTURE)
        mode = mode.strip().casefold()
        if mode not in CAPTURE_MODES:
            raise ValueError("screen.capture mode must be active_window or primary_screen")
        raw, width, height, title = _capture_bmp(mode)
        self.directory.mkdir(parents=True, exist_ok=True)
        observation_id = uuid.uuid4().hex
        target = self.directory / f"{observation_id}.bmp"
        with target.open("xb") as stream:
            stream.write(raw)
        observation = ScreenObservation(
            id=observation_id,
            mode=mode,
            path=str(target),
            width=width,
            height=height,
            captured_at=datetime.now(timezone.utc).isoformat(),
            window_title=title[:512],
        )
        records = self.list()
        records.append(observation)
        records = records[-20:]
        self.manifest_path.write_text(
            json.dumps([asdict(item) for item in records], ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return observation

    def list(self) -> list[ScreenObservation]:
        if not self.manifest_path.exists():
            return []
        try:
            data = json.loads(self.manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []
        if not isinstance(data, list):
            return []
        records: list[ScreenObservation] = []
        for item in data:
            if not isinstance(item, dict):
                continue
            try:
                records.append(ScreenObservation(**item))
            except (TypeError, ValueError):
                continue
        return records

    def get(self, observation_id: str) -> ScreenObservation | None:
        return next((item for item in self.list() if item.id == observation_id), None)

    def delete(self, observation_id: str) -> bool:
        records = self.list()
        selected = next((item for item in records if item.id == observation_id), None)
        if selected is None:
            return False
        target = Path(selected.path)
        if target.parent.resolve() != self.directory.resolve():
            raise ValueError("Stored capture path escaped the capture directory")
        target.unlink(missing_ok=True)
        kept = [item for item in records if item.id != observation_id]
        self.manifest_path.write_text(
            json.dumps([asdict(item) for item in kept], ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return True
