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


def test_message_fallback_uses_explicit_peer_id_and_photo_field(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")
    api_calls: list[tuple[str, dict[str, object]]] = []
    upload_calls: list[tuple[str, Path, str]] = []

    monkeypatch.setenv("VK_IMAGE_PEER_ID", "42")

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
    assert api_calls[0] == ("photos.getMessagesUploadServer", {"peer_id": 42})
    assert api_calls[1][0] == "photos.saveMessagesPhoto"
    assert upload_calls == [("https://upload.example/messages", image, "photo")]


def test_peer_discovery_uses_only_writable_user_dialog(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)

    def fake_api(method: str, params: dict[str, object], token: str) -> object:
        assert method == "messages.getConversations"
        assert params == {"count": 20, "filter": "all", "extended": 0}
        assert token == "group-token"
        return {
            "count": 3,
            "items": [
                {
                    "conversation": {
                        "peer": {"id": 123, "type": "user"},
                        "can_write": {"allowed": True},
                    }
                },
                {
                    "conversation": {
                        "peer": {"id": 2000000001, "type": "chat"},
                        "can_write": {"allowed": True},
                    }
                },
                {
                    "conversation": {
                        "peer": {"id": 456, "type": "user"},
                        "can_write": {"allowed": False},
                    }
                },
            ],
        }

    monkeypatch.setattr(MODULE.VK, "vk_api_call", fake_api)

    assert MODULE._resolve_peer_id("group-token") == 123


def test_peer_discovery_uses_unique_recent_incoming_dialog(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)
    monkeypatch.setattr(MODULE.time, "time", lambda: 2_000_000)
    monkeypatch.setattr(
        MODULE.VK,
        "vk_api_call",
        lambda *_args, **_kwargs: {
            "count": 2,
            "items": [
                {
                    "conversation": {
                        "peer": {"id": 123, "type": "user"},
                        "can_write": {"allowed": True},
                    },
                    "last_message": {"from_id": 123, "date": 1_990_000, "out": 0},
                },
                {
                    "conversation": {
                        "peer": {"id": 456, "type": "user"},
                        "can_write": {"allowed": True},
                    },
                    "last_message": {"from_id": 456, "date": 1_999_940, "out": 0},
                },
            ],
        },
    )

    assert MODULE._resolve_peer_id("group-token") == 456


def test_peer_discovery_fails_when_no_writable_user_dialog(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)
    monkeypatch.setattr(
        MODULE.VK,
        "vk_api_call",
        lambda *_args, **_kwargs: {"count": 0, "items": []},
    )

    with pytest.raises(MODULE.VK.VkPublishError, match="private message"):
        MODULE._resolve_peer_id("group-token")


def test_peer_discovery_fails_closed_when_multiple_recent_dialogs(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)
    monkeypatch.setattr(MODULE.time, "time", lambda: 2_000_000)
    monkeypatch.setattr(
        MODULE.VK,
        "vk_api_call",
        lambda *_args, **_kwargs: {
            "count": 2,
            "items": [
                {
                    "conversation": {
                        "peer": {"id": 123, "type": "user"},
                        "can_write": {"allowed": True},
                    },
                    "last_message": {"from_id": 123, "date": 1_999_900, "out": 0},
                },
                {
                    "conversation": {
                        "peer": {"id": 456, "type": "user"},
                        "can_write": {"allowed": True},
                    },
                    "last_message": {"from_id": 456, "date": 1_999_940, "out": 0},
                },
            ],
        },
    )

    with pytest.raises(MODULE.VK.VkPublishError, match="recently active"):
        MODULE._resolve_peer_id("group-token")


def test_peer_discovery_fails_closed_when_multiple_stale_dialogs(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("VK_IMAGE_PEER_ID", raising=False)
    monkeypatch.setattr(MODULE.time, "time", lambda: 2_000_000)
    monkeypatch.setattr(
        MODULE.VK,
        "vk_api_call",
        lambda *_args, **_kwargs: {
            "count": 2,
            "items": [
                {
                    "conversation": {
                        "peer": {"id": 123, "type": "user"},
                        "can_write": {"allowed": True},
                    }
                },
                {
                    "conversation": {
                        "peer": {"id": 456, "type": "user"},
                        "can_write": {"allowed": True},
                    }
                },
            ],
        },
    )

    with pytest.raises(MODULE.VK.VkPublishError, match="none has a unique recent incoming message"):
        MODULE._resolve_peer_id("group-token")


@pytest.mark.parametrize("value", ["-42", "0", "nope"])
def test_invalid_explicit_peer_id_is_rejected(
    value: str,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("VK_IMAGE_PEER_ID", value)

    with pytest.raises(MODULE.VK.VkPublishError):
        MODULE._resolve_peer_id("group-token")
