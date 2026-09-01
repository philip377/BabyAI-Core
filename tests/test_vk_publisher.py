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
    post_file = tmp_path / "first.post.json"
    post_file.write_text(
        json.dumps(
            {
                "message": "  Привет из UNIX  ",
                "attachments": ["photo-1_2", " https://example.com "],
                "guid": "unix-first-post",
                "close_comments": False,
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    post = VK.load_post(post_file)

    assert post["message"] == "Привет из UNIX"
    assert post["attachments"] == ["photo-1_2", "https://example.com"]
    assert post["guid"] == "unix-first-post"
    assert post["close_comments"] is False


def test_empty_post_is_rejected(tmp_path: Path) -> None:
    post_file = tmp_path / "empty.post.json"
    post_file.write_text('{"message": "   "}', encoding="utf-8")

    with pytest.raises(VK.VkPublishError):
        VK.load_post(post_file)


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
    assert params["v"] == "5.199"
