from __future__ import annotations

import json
from dataclasses import asdict

import pytest

from babyai.ginger_local_vision import (
    GingerVisualObservation, LocalGingerVision, LocalVisionError,
    parse_ginger_visual_reply,
)
from babyai.permissions import PermissionStore
from babyai.screen_vision import ScreenCaptureStore, ScreenObservation


CAPTURE_ID = "a" * 32


def _store(tmp_path):
    directory = tmp_path / "captures"
    directory.mkdir()
    (directory / (CAPTURE_ID + ".bmp")).write_bytes(b"BM" + b"\0" * 52)
    observation = ScreenObservation(
        id=CAPTURE_ID, mode="active_window",
        path=str(directory / (CAPTURE_ID + ".bmp")), width=100,
        height=100, captured_at="2026-10-23T00:00:00Z",
        window_title="Ginger4",
    )
    (directory / "observations.json").write_text(
        json.dumps([asdict(observation)]), encoding="utf-8"
    )
    return ScreenCaptureStore(directory, PermissionStore(tmp_path / "permissions.json"))


def _reply():
    return json.dumps({
        "selected_supplier": "Bruno Banani Man Intense edP 30ml",
        "starred_words": ["Bruno", "Banani", "Man", "Intense", "edP", "30", "ml"],
        "visible_catalog_candidates": ["BRUNO BANANI (муж) edp 30ml"],
        "status": "ambiguous",
        "warnings": ["Visible base perfume is not the INTENSE variant."],
    })


def test_local_multimodal_request_is_loopback_only_and_read_only(tmp_path, monkeypatch):
    store = _store(tmp_path)
    sent = {}

    class FakeResponse:
        def __enter__(self): return self
        def __exit__(self, *_): return False
        def read(self, n): return json.dumps({
            "choices": [{"message": {"content": _reply()}}]
        }).encode("utf-8")

    class FakeOpener:
        def open(self, request, timeout):
            sent["url"] = request.full_url
            sent["method"] = request.get_method()
            sent["payload"] = json.loads(request.data.decode("utf-8"))
            sent["timeout"] = timeout
            return FakeResponse()

    monkeypatch.setattr(
        "babyai.ginger_local_vision.urllib.request.build_opener",
        lambda *handlers: FakeOpener(),
    )
    before = (store.directory / (CAPTURE_ID + ".bmp")).read_bytes()
    result = LocalGingerVision(store).analyze(CAPTURE_ID)
    assert result.status == "ambiguous"
    assert result.selected_supplier == "Bruno Banani Man Intense edP 30ml"
    assert result.visible_catalog_candidates == ("BRUNO BANANI (муж) edp 30ml",)
    assert result.as_dict()["automated_linking"] is False
    assert sent["url"] == "http://127.0.0.1:8080/v1/chat/completions"
    assert sent["method"] == "POST"
    content = sent["payload"]["messages"][0]["content"]
    assert content[1]["type"] == "image_url"
    assert content[1]["image_url"]["url"].startswith("data:image/bmp;base64,")
    assert "INTENSE" in content[0]["text"]
    assert (store.directory / (CAPTURE_ID + ".bmp")).read_bytes() == before
    assert sent["timeout"] == 60


def test_local_vision_rejects_non_authoritative_output():
    value = json.loads(_reply())
    value["command"] = {"click": [100, 200]}
    with pytest.raises(LocalVisionError, match="unauthorized"):
        parse_ginger_visual_reply(CAPTURE_ID, json.dumps(value))
    value.pop("command")
    value["status"] = "linked"
    with pytest.raises(LocalVisionError, match="status"):
        parse_ginger_visual_reply(CAPTURE_ID, json.dumps(value))
    value["status"] = "ambiguous"
    value["visible_catalog_candidates"] = ["x"] * 21
    with pytest.raises(LocalVisionError, match="visible_catalog_candidates"):
        parse_ginger_visual_reply(CAPTURE_ID, json.dumps(value))


def test_model_unavailable_and_bad_capture_fail_closed(tmp_path):
    store = _store(tmp_path)
    with pytest.raises(LocalVisionError, match="capture ID"):
        LocalGingerVision(store).analyze("../../secret")
    with pytest.raises(LocalVisionError, match="port"):
        LocalGingerVision(store, port=80)
    (store.directory / (CAPTURE_ID + ".bmp")).write_bytes(b"invalid")
    with pytest.raises(LocalVisionError, match="size"):
        LocalGingerVision(store).analyze(CAPTURE_ID)


def test_symlinked_capture_is_rejected(tmp_path):
    store = _store(tmp_path)
    path = store.directory / (CAPTURE_ID + ".bmp")
    path.unlink()
    secret = tmp_path / "private.bmp"
    secret.write_bytes(b"BM" + b"\0" * 52)
    try:
        path.symlink_to(secret)
    except OSError:
        pytest.skip("Symlink creation not available")
    with pytest.raises(LocalVisionError, match="directory"):
        LocalGingerVision(store).analyze(CAPTURE_ID)


def test_json_markdown_fences_are_supported_but_plain_prose_is_not():
    fence = chr(96) * 3
    structured = parse_ginger_visual_reply(CAPTURE_ID, fence + "json\n" + _reply() + "\n" + fence)
    assert isinstance(structured, GingerVisualObservation)
    with pytest.raises(LocalVisionError, match="valid JSON"):
        parse_ginger_visual_reply(CAPTURE_ID, "I will click on the row now.")


def test_invalid_model_response_is_not_used(tmp_path, monkeypatch):
    store = _store(tmp_path)

    class FakeResponse:
        def __enter__(self): return self
        def __exit__(self, *_): return False
        def read(self, n): return b'{"choices":[]}'

    class FakeOpener:
        def open(self, request, timeout): return FakeResponse()

    monkeypatch.setattr(
        "babyai.ginger_local_vision.urllib.request.build_opener",
        lambda *args: FakeOpener(),
    )
    with pytest.raises(LocalVisionError, match="invalid|Invalid"):
        LocalGingerVision(store).analyze(CAPTURE_ID)
