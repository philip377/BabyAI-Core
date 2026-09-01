#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

VK_API_URL = "https://api.vk.com/method/wall.post"
VK_API_VERSION = "5.199"


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


def load_post(path: Path) -> dict[str, Any]:
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

    if not message and not attachments:
        raise VkPublishError("A VK post needs 'message' and/or 'attachments'")

    guid = payload.get("guid") or path.stem
    if not isinstance(guid, str) or not guid.strip():
        raise VkPublishError("'guid' must be a non-empty string")

    normalized: dict[str, Any] = {
        "message": message,
        "attachments": attachments,
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


def build_params(post: dict[str, Any], group_id: str | int, token: str) -> dict[str, str]:
    params = {
        "access_token": token,
        "v": VK_API_VERSION,
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


def publish(post: dict[str, Any], group_id: str | int, token: str) -> tuple[int, str]:
    if not token:
        raise VkPublishError("VK access token is empty")

    params = build_params(post, group_id, token)
    owner_id = params["owner_id"]
    request = urllib.request.Request(
        VK_API_URL,
        data=urllib.parse.urlencode(params).encode("utf-8"),
        headers={
            "Content-Type": "application/x-www-form-urlencoded",
            "User-Agent": "BabyAI-Core-VK-Publisher/1.0",
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

    try:
        post_id = int(result["response"]["post_id"])
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
