from __future__ import annotations

import importlib.util
import json
from pathlib import Path

import pytest


MODULE_PATH = Path(__file__).resolve().parents[1] / "scripts" / "social" / "vk_publish.py"
SPEC = importlib.util.spec_from_file_location("vk_publish", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
VK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VK)


def test_group_id_is_always_community_owner_id() -> None:
    assert VK.normalize_group_id("123456") == -123456
    assert VK.normalize_group_id("-123456") == -123456


@pytest.mark.parametrize("value", ["0", "nope", ""])
def test_invalid_group_id_is_rejected(value: str) -> None:
    with pytest.raises(VK.VkPublishError):
        VK.normalize_group_id(value)


def test_load_post_normalizes_payload(tmp_path: Path) -> None:
    assets = tmp_path / "social" / "vk" / "assets"
    assets.mkdir(parents=True)
    (assets / "orb.png").write_bytes(b"png")

    post_file = tmp_path / "first.post.json"
    post_file.write_text(
        json.dumps(
            {
                "message": "  Привет из UNIX  ",
                "attachments": ["photo-1_2", " https://example.com "],
                "image_paths": ["social/vk/assets/orb.png"],
                "guid": "unix-first-post",
                "close_comments": False,
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    post = VK.load_post(post_file, repo_root=tmp_path)

    assert post["message"] == "Привет из UNIX"
    assert post["attachments"] == ["photo-1_2", "https://example.com"]
    assert post["image_paths"] == ["social/vk/assets/orb.png"]
    assert post["guid"] == "unix-first-post"
    assert post["close_comments"] is False


def test_image_only_post_is_allowed(tmp_path: Path) -> None:
    assets = tmp_path / "social" / "vk" / "assets"
    assets.mkdir(parents=True)
    (assets / "orb.jpg").write_bytes(b"jpg")
    post_file = tmp_path / "image.post.json"
    post_file.write_text(
        json.dumps({"image_paths": ["social/vk/assets/orb.jpg"]}),
        encoding="utf-8",
    )

    post = VK.load_post(post_file, repo_root=tmp_path)
    assert post["message"] == ""
    assert post["image_paths"] == ["social/vk/assets/orb.jpg"]


@pytest.mark.parametrize(
    "image_path",
    [
        "../secret.png",
        "README.md",
        "social/vk/assets/../../secret.png",
        "/tmp/secret.png",
        "social/vk/assets/file.txt",
    ],
)
def test_unsafe_image_paths_are_rejected(tmp_path: Path, image_path: str) -> None:
    assets = tmp_path / "social" / "vk" / "assets"
    assets.mkdir(parents=True)
    (tmp_path / "README.md").write_text("x", encoding="utf-8")
    (assets / "file.txt").write_text("x", encoding="utf-8")

    with pytest.raises(VK.VkPublishError):
        VK.normalize_image_paths([image_path], repo_root=tmp_path)


def test_missing_image_is_rejected(tmp_path: Path) -> None:
    (tmp_path / "social" / "vk" / "assets").mkdir(parents=True)
    with pytest.raises(VK.VkPublishError):
        VK.normalize_image_paths(["social/vk/assets/missing.png"], repo_root=tmp_path)


def test_too_many_images_are_rejected(tmp_path: Path) -> None:
    with pytest.raises(VK.VkPublishError):
        VK.normalize_image_paths(
            [f"social/vk/assets/{index}.png" for index in range(VK.MAX_IMAGES_PER_POST + 1)],
            repo_root=tmp_path,
        )


def test_empty_post_is_rejected(tmp_path: Path) -> None:
    post_file = tmp_path / "empty.post.json"
    post_file.write_text('{"message": "   "}', encoding="utf-8")

    with pytest.raises(VK.VkPublishError):
        VK.load_post(post_file, repo_root=tmp_path)


def test_build_params_posts_as_group() -> None:
    params = VK.build_params(
        {
            "message": "Hello",
            "attachments": [],
            "guid": "test-guid",
        },
        "42",
        "secret-token",
    )

    assert params["owner_id"] == "-42"
    assert params["from_group"] == "1"
    assert params["message"] == "Hello"
    assert params["guid"] == "test-guid"


def test_photo_attachment_includes_access_key() -> None:
    assert VK._photo_attachment({"owner_id": -42, "id": 99}) == "photo-42_99"
    assert (
        VK._photo_attachment({"owner_id": -42, "id": 99, "access_key": "abc"})
        == "photo-42_99_abc"
    )


def test_multipart_image_uses_requested_field(tmp_path: Path) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")

    wall_body, _ = VK._build_multipart_image(image)
    message_body, _ = VK._build_multipart_image(image, field_name="file")

    assert b'name="photo"' in wall_body
    assert b'name="file"' in message_body
    assert b'name="photo"' not in message_body


def test_group_auth_error_falls_back_to_messages_photo(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")
    calls: list[tuple[str, dict[str, object]]] = []
    upload_calls: list[tuple[str, Path, str]] = []

    def fake_api(method: str, params: dict[str, object], token: str) -> object:
        assert token == "group-token"
        calls.append((method, params))
        if method == "photos.getWallUploadServer":
            raise VK.VkApiError(27, "Group authorization failed")
        if method == "photos.getMessagesUploadServer":
            return {"upload_url": "https://upload.example/messages"}
        if method == "photos.saveMessagesPhoto":
            return [{"owner_id": -42, "id": 99, "access_key": "key"}]
        raise AssertionError(method)

    def fake_upload(upload_url: str, path: Path, field_name: str = "photo") -> dict[str, object]:
        upload_calls.append((upload_url, path, field_name))
        return {
            "server": 1,
            "photo": "photo-json",
            "hash": "hash",
        }

    monkeypatch.setattr(VK, "vk_api_call", fake_api)
    monkeypatch.setattr(VK, "_upload_image_bytes", fake_upload)

    attachment = VK.upload_wall_image(image, "42", "group-token")

    assert attachment == "photo-42_99_key"
    assert [method for method, _params in calls] == [
        "photos.getWallUploadServer",
        "photos.getMessagesUploadServer",
        "photos.saveMessagesPhoto",
    ]
    assert upload_calls == [("https://upload.example/messages", image, "file")]


def test_non_group_auth_error_does_not_fallback(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = tmp_path / "orb.jpg"
    image.write_bytes(b"jpg")

    def fake_api(method: str, params: dict[str, object], token: str) -> object:
        raise VK.VkApiError(5, "Authorization failed")

    monkeypatch.setattr(VK, "vk_api_call", fake_api)

    with pytest.raises(VK.VkApiError) as exc:
        VK.upload_wall_image(image, "42", "token")

    assert exc.value.code == 5


def test_prepare_attachments_adds_uploaded_images(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    assets = tmp_path / "social" / "vk" / "assets"
    assets.mkdir(parents=True)
    image = assets / "orb.png"
    image.write_bytes(b"png")

    calls: list[Path] = []

    def fake_upload(path: Path, group_id: str | int, token: str) -> str:
        calls.append(path)
        assert group_id == "42"
        assert token == "secret"
        return "photo-42_99"

    monkeypatch.setattr(VK, "upload_wall_image", fake_upload)

    attachments = VK.prepare_attachments(
        {
            "attachments": ["https://example.com"],
            "image_paths": ["social/vk/assets/orb.png"],
        },
        "42",
        "secret",
        repo_root=tmp_path,
    )

    assert attachments == ["https://example.com", "photo-42_99"]
    assert calls == [image.resolve()]


def test_publish_posts_uploaded_photo_attachment(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    assets = tmp_path / "social" / "vk" / "assets"
    assets.mkdir(parents=True)
    (assets / "orb.png").write_bytes(b"png")

    monkeypatch.setattr(VK, "upload_wall_image", lambda *_args, **_kwargs: "photo-42_99")
    seen: dict[str, object] = {}

    def fake_api(method: str, params: dict[str, object], token: str) -> object:
        seen["method"] = method
        seen["params"] = params
        seen["token"] = token
        return {"post_id": 7}

    monkeypatch.setattr(VK, "vk_api_call", fake_api)

    post_id, url = VK.publish(
        {
            "message": "Photo test",
            "attachments": [],
            "image_paths": ["social/vk/assets/orb.png"],
            "guid": "photo-test",
        },
        "42",
        "secret",
        repo_root=tmp_path,
    )

    assert post_id == 7
    assert url == "https://vk.com/wall-42_7"
    assert seen["method"] == "wall.post"
    assert seen["token"] == "secret"
    assert seen["params"]["attachments"] == "photo-42_99"
