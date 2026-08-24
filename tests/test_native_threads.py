from __future__ import annotations

import babyai.native_threads as native_threads


def test_preferred_native_threads_use_physical_cores_when_available(monkeypatch):
    monkeypatch.setattr(native_threads, "_windows_physical_cpu_count", lambda: 4)

    assert native_threads.preferred_native_thread_count(logical_cpu_count=8) == 4


def test_preferred_native_threads_fall_back_to_logical_cores(monkeypatch):
    monkeypatch.setattr(native_threads, "_windows_physical_cpu_count", lambda: None)

    assert native_threads.preferred_native_thread_count(logical_cpu_count=6) == 6


def test_preferred_native_threads_cap_large_cpu_counts(monkeypatch):
    monkeypatch.setattr(native_threads, "_windows_physical_cpu_count", lambda: 16)

    assert native_threads.preferred_native_thread_count(logical_cpu_count=32) == 8
