#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any

VK_API_BASE = "https://api.vk.com/method"
VK_API_VERSION = "5.199"
REPO_ROOT = Path(__file__).resolve().parents[2]
VK_ASSETS_DIR = Path("social/vk/assets")
ALLOWED_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp"}
MAX_IMAGES_PER_POST = 10


class VkPublishError(RuntimeError):
    pass


def normalize_group_id(value: str | int) -> int:
    try:
        group_id = int(value)
    except (TypeError, ValueError) as exc:
        raise VkPublishError("VK group id must be an integer") from exc

    if group_id == 0:
        raise VkPublishError("VK group id must not be zero")

    return -abs(group_id)


def normalize_image_paths(value: Any, repo_root: Path = REPO_ROOT) -> list[str]:
    if value is None:
        return []
    if isinstance(value, str):
        value = [value]
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        raise VkPublishError("'image_paths' must be a string or a list of strings")
    if len(value) > MAX_IMAGES_PER_POST:
        raise VkPublishError(f"'image_paths' supports at most {MAX_IMAGES_PER_POST} images")

    repo_root = repo_root.resolve()
    assets_root = (repo_root / VK_ASSETS_DIR).resolve()
    normalized: list[str] = []

    for raw in value:
        item = raw.strip()
        if not item:
            continue

        rel = Path(item)
        if rel.is_absolute() or ".." in rel.parts:
            raise VkPublishError("Image paths must stay inside social/vk/assets")

        full = (repo_root / rel).resolve()
        if not full.is_relative_to(assets_root):
            raise VkPublishError("Image paths must stay inside social/vk/assets")
        if full.suffix.lower() not in ALLOWED_IMAGE_EXTENSIONS:
            allowed = ", ".join(sorted(ALLOWED_IMAGE_EXTENSIONS))
            raise VkPublishError(f"VK images must use one of: {allowed}")
        if not full.is_file():
            raise VkPublishError(f"VK image does not exist: {rel.as_posix()}")

        normalized.append(rel.as_posix())

    return normalized


def load_post(path: Path, repo_root: Path = REPO_ROOT) -> dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise VkPublishError(f"Post file does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise VkPublishError(f"Invalid JSON in {path}: {exc}") from exc

    if not isinstance(payload, dict):
        raise VkPublishError("Post payload must be a JSON object")

    message = payload.get("message", "")
    if not isinstance(message, str):
        raise VkPublishError("'message' must be a string")
    message = message.strip()

    attachments = payload.get("attachments", [])
    if isinstance(attachments, str):
        attachments = [attachments]
    if not isinstance(attachments, list) or not all(isinstance(item, str) for item in attachments):
        raise VkPublishError("'attachments' must be a string or a list of strings")
    attachments = [item.strip() for item in attachments if item.strip()]

    image_paths = normalize_image_paths(payload.get("image_paths"), repo_root)

    if not message and not attachments and not image_paths:
        raise VkPublishError("A VK post needs 'message', 'attachments', and/or 'image_paths'")

    guid = payload.get("guid") or path.stem
    if not isinstance(guid, str) or not guid.strip():
        raise VkPublishError("'guid' must be a non-empty string")

    normalized: dict[str, Any] = {
        "message": message,
        "attachments": attachments,
        "image_paths": image_paths,
        "guid": guid.strip(),
    }

    if "publish_date" in payload:
        try:
            normalized["publish_date"] = int(payload["publish_date"])
        except (TypeError, ValueError) as exc:
            raise VkPublishError("'publish_date' must be a Unix timestamp") from exc

    for key in ("close_comments", "signed"):
        if key in payload:
            if not isinstance(payload[key], bool):
                raise VkPublishError(f"'{key}' must be true or false")
            normalized[key] = payload[key]

    return normalized


def vk_api_call(method: str, params: dict[str, Any], token: str) -> Any:
    if not token:
        raise VkPublishError("VK access token is empty")

    request_params = {
        "access_token": token,
        "v": VK_API_VERSION,
        **{key: str(value) for key, value in params.items()},
    }
    request = urllib.request.Request(
        f"{VK_API_BASE}/{method}",
        data=urllib.parse.urlencode(request_params).encode("utf-8"),
        headers={
            "Content-Type": "application/x-www-form-urlencoded",
            "User-Agent": "BabyAI-Core-VK-Publisher/1.1",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            result = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raise VkPublishError(f"VK HTTP error: {exc.code}") from exc
    except urllib.error.URLError as exc:
        raise VkPublishError(f"VK network error: {exc.reason}") from exc
    except json.JSONDecodeError as exc:
        raise VkPublishError("VK returned a non-JSON response") from exc

    if "error" in result:
        error = result["error"]
        code = error.get("error_code", "unknown")
        message = error.get("error_msg", "Unknown VK API error")
        raise VkPublishError(f"VK API error {code}: {message}")

    if "response" not in result:
        raise VkPublishError(f"Unexpected VK response: {result}")
    return result["response"]


def _build_multipart_image(path: Path) -> tuple[bytes, str]:
    boundary = f"----BabyAIVK{uuid.uuid4().hex}"
    content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    safe_filename = path.name.replace('"', "_").replace("\r", "_").replace("\n", "_")
    file_bytes = path.read_bytes()

    body = b"".join(
        [
            f"--{boundary}\r\n".encode("ascii"),
            f'Content-Disposition: form-data; name="photo"; filename="{safe_filename}"\r\n'.encode("utf-8"),
            f"Content-Type: {content_type}\r\n\r\n".encode("ascii"),
            file_bytes,
            b"\r\n",
            f"--{boundary}--\r\n".encode("ascii"),
        ]
    )
    return body, boundary


def _upload_image_bytes(upload_url: str, path: Path) -> dict[str, Any]:
    body, boundary = _build_multipart_image(path)
    request = urllib.request.Request(
        upload_url,
        data=body,
        headers={
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "User-Agent": "BabyAI-Core-VK-Publisher/1.1",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            result = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raise VkPublishError(f"VK image upload HTTP error: {exc.code}") from exc
    except urllib.error.URLError as exc:
        raise VkPublishError(f"VK image upload network error: {exc.reason}") from exc
    except json.JSONDecodeError as exc:
        raise VkPublishError("VK image upload returned a non-JSON response") from exc

    if not isinstance(result, dict):
        raise VkPublishError(f"Unexpected VK image upload response: {result}")
    for key in ("server", "photo", "hash"):
        if key not in result:
            raise VkPublishError(f"VK image upload response is missing '{key}'")
    return result


def upload_wall_image(path: Path, group_id: str | int, token: str) -> str:
    owner_id = normalize_group_id(group_id)
    positive_group_id = abs(owner_id)

    upload_server = vk_api_call(
        "photos.getWallUploadServer",
        {"group_id": positive_group_id},
        token,
    )
    try:
        upload_url = str(upload_server["upload_url"])
    except (KeyError, TypeError) as exc:
        raise VkPublishError(f"Unexpected VK upload-server response: {upload_server}") from exc

    uploaded = _upload_image_bytes(upload_url, path)
    saved = vk_api_call(
        "photos.saveWallPhoto",
        {
            "group_id": positive_group_id,
            "server": uploaded["server"],
            "photo": uploaded["photo"],
            "hash": uploaded["hash"],
        },
        token,
    )
    if not isinstance(saved, list) or not saved:
        raise VkPublishError(f"Unexpected VK save-photo response: {saved}")

    photo = saved[0]
    try:
        photo_owner_id = int(photo["owner_id"])
        photo_id = int(photo["id"])
    except (KeyError, TypeError, ValueError) as exc:
        raise VkPublishError(f"Unexpected VK saved-photo object: {photo}") from exc

    return f"photo{photo_owner_id}_{photo_id}"


def prepare_attachments(
    post: dict[str, Any],
    group_id: str | int,
    token: str,
    repo_root: Path = REPO_ROOT,
) -> list[str]:
    attachments = list(post.get("attachments", []))
    for relative_path in post.get("image_paths", []):
        image_path = (repo_root / relative_path).resolve()
        attachments.append(upload_wall_image(image_path, group_id, token))
    return attachments


def build_params(
    post: dict[str, Any],
    group_id: str | int,
    token: str | None = None,
) -> dict[str, str]:
    params = {
        "owner_id": str(normalize_group_id(group_id)),
        "from_group": "1",
        "guid": str(post["guid"]),
    }

    if post.get("message"):
        params["message"] = str(post["message"])
    if post.get("attachments"):
        params["attachments"] = ",".join(post["attachments"])
    if "publish_date" in post:
        params["publish_date"] = str(post["publish_date"])
    if "close_comments" in post:
        params["close_comments"] = "1" if post["close_comments"] else "0"
    if "signed" in post:
        params["signed"] = "1" if post["signed"] else "0"

    return params


def publish(
    post: dict[str, Any],
    group_id: str | int,
    token: str,
    repo_root: Path = REPO_ROOT,
) -> tuple[int, str]:
    if not token:
        raise VkPublishError("VK access token is empty")

    enriched_post = dict(post)
    enriched_post["attachments"] = prepare_attachments(post, group_id, token, repo_root)
    params = build_params(enriched_post, group_id, token)
    owner_id = params["owner_id"]
    result = vk_api_call("wall.post", params, token)

    try:
        post_id = int(result["post_id"])
    except (KeyError, TypeError, ValueError) as exc:
        raise VkPublishError(f"Unexpected VK response: {result}") from exc

    return post_id, f"https://vk.com/wall{owner_id}_{post_id}"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Publish one JSON post to a VK community wall")
    parser.add_argument("post_file", type=Path)
    parser.add_argument("--dry-run", action="store_true", help="Validate the post without calling VK")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])

    try:
        post = load_post(args.post_file)
        group_id = os.environ.get("VK_GROUP_ID", "")

        if args.dry_run:
            owner_id = normalize_group_id(group_id or "1")
            print(
                json.dumps(
                    {
                        "owner_id": owner_id,
                        "from_group": 1,
                        "guid": post["guid"],
                        "has_message": bool(post["message"]),
                        "attachment_count": len(post["attachments"]),
                        "image_count": len(post["image_paths"]),
                    },
                    ensure_ascii=False,
                )
            )
            return 0

        token = os.environ.get("VK_ACCESS_TOKEN", "")
        if not group_id:
            raise VkPublishError("VK_GROUP_ID is not configured")
        if not token:
            raise VkPublishError("VK_ACCESS_TOKEN is not configured")

        post_id, url = publish(post, group_id, token)
        print(f"Published VK post {post_id}: {url}")
        return 0
    except VkPublishError as exc:
        print(f"VK publish failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
