from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts" / "social" / "vk_publish_group_image.py"
SPEC = importlib.util.spec_from_file_location("vk_publish_group_image", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def test_message_fallback_uses_peer_id_and_file_field(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")
    api_calls: list[tuple[str, dict[str, object]]] = []
    upload_calls: list[tuple[str, Path, str]] = []

    monkeypatch.setenv("VK_IMAGE_PEER_ID", "-42")

    def fake_api(method: str, params: dict[str, object], token: str) -> object:
        assert token == "group-token"
        api_calls.append((method, params))
        if method == "photos.getMessagesUploadServer":
            return {"upload_url": "https://upload.example/messages"}
        if method == "photos.saveMessagesPhoto":
            return [{"owner_id": -42, "id": 99, "access_key": "key"}]
        raise AssertionError(method)

    def fake_upload(upload_url: str, path: Path, field_name: str = "photo") -> dict[str, object]:
        upload_calls.append((upload_url, path, field_name))
        return {"server": 1, "photo": "photo-json", "hash": "hash"}

    monkeypatch.setattr(MODULE.VK, "vk_api_call", fake_api)
    monkeypatch.setattr(MODULE.VK, "_upload_image_bytes", fake_upload)

    attachment = MODULE._message_photo_with_peer(image, "group-token")

    assert attachment == "photo-42_99_key"
    assert api_calls[0] == ("photos.getMessagesUploadServer", {"peer_id": -42})
    assert api_calls[1][0] == "photos.saveMessagesPhoto"
    assert upload_calls == [("https://upload.example/messages", image, "file")]


def test_message_fallback_requires_peer_id(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)

    with pytest.raises(MODULE.VK.VkPublishError):
        MODULE._message_photo_with_peer(image, "group-token")
