#!/usr/bin/env python3
from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any

import vk_publish as VK


def _message_photo_with_peer(path: Path, token: str) -> str:
    raw_peer_id = os.environ.get("VK_IMAGE_PEER_ID", "").strip()
    if not raw_peer_id:
        raise VK.VkPublishError("VK_IMAGE_PEER_ID is required for the group-token image fallback")

    try:
        peer_id = int(raw_peer_id)
    except ValueError as exc:
        raise VK.VkPublishError("VK_IMAGE_PEER_ID must be an integer") from exc

    upload_server = VK.vk_api_call(
        "photos.getMessagesUploadServer",
        {"peer_id": peer_id},
        token,
    )
    try:
        upload_url = str(upload_server["upload_url"])
    except (KeyError, TypeError) as exc:
        raise VK.VkPublishError(
            f"Unexpected VK messages upload-server response: {upload_server}"
        ) from exc

    uploaded = VK._upload_image_bytes(upload_url, path, field_name="file")
    saved = VK.vk_api_call(
        "photos.saveMessagesPhoto",
        {
            "server": uploaded["server"],
            "photo": uploaded["photo"],
            "hash": uploaded["hash"],
        },
        token,
    )
    if not isinstance(saved, list) or not saved:
        raise VK.VkPublishError(f"Unexpected VK save-messages-photo response: {saved}")

    return VK._photo_attachment(saved[0])


def main(argv: list[str] | None = None) -> int:
    # Keep the normal publisher untouched except for the narrow fallback used
    # after VK rejects photos.getWallUploadServer for a community token.
    VK.upload_message_image = _message_photo_with_peer  # type: ignore[assignment]
    return VK.main(argv or sys.argv[1:])


if __name__ == "__main__":
    raise SystemExit(main())
