#!/usr/bin/env python3
from __future__ import annotations

import os
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import vk_publish as VK


RECENT_INCOMING_WINDOW_SECONDS = 30 * 60


def _explicit_peer_id() -> int | None:
    raw_peer_id = os.environ.get("VK_IMAGE_PEER_ID", "").strip()
    if not raw_peer_id:
        return None

    try:
        peer_id = int(raw_peer_id)
    except ValueError as exc:
        raise VK.VkPublishError("VK_IMAGE_PEER_ID must be an integer") from exc

    if peer_id <= 0:
        raise VK.VkPublishError("VK_IMAGE_PEER_ID must identify a user and be greater than zero")
    return peer_id


def _discover_single_writable_user_peer(token: str) -> int:
    response = VK.vk_api_call(
        "messages.getConversations",
        {
            "count": 20,
            "filter": "all",
            "extended": 0,
        },
        token,
    )

    if not isinstance(response, dict):
        raise VK.VkPublishError(f"Unexpected VK conversations response: {response}")

    items = response.get("items")
    if not isinstance(items, list):
        raise VK.VkPublishError(f"Unexpected VK conversations response: {response}")

    dialogs: list[tuple[int, dict[str, object]]] = []
    seen_peers: set[int] = set()
    for item in items:
        if not isinstance(item, dict):
            continue
        conversation = item.get("conversation")
        if not isinstance(conversation, dict):
            continue
        peer = conversation.get("peer")
        if not isinstance(peer, dict) or peer.get("type") != "user":
            continue
        can_write = conversation.get("can_write")
        if isinstance(can_write, dict) and can_write.get("allowed") is False:
            continue
        peer_id = peer.get("id")
        if isinstance(peer_id, int) and peer_id > 0 and peer_id not in seen_peers:
            seen_peers.add(peer_id)
            dialogs.append((peer_id, item))

    if not dialogs:
        raise VK.VkPublishError(
            "No writable user dialog is available for VK image upload. "
            "Send the community a private message first."
        )
    if len(dialogs) == 1:
        return dialogs[0][0]

    now = int(time.time())
    recent_incoming: list[int] = []
    for peer_id, item in dialogs:
        last_message = item.get("last_message")
        if not isinstance(last_message, dict):
            continue
        from_id = last_message.get("from_id")
        date = last_message.get("date")
        out = last_message.get("out")
        if from_id != peer_id or not isinstance(date, int):
            continue
        if isinstance(out, int) and out != 0:
            continue
        age = now - date
        if 0 <= age <= RECENT_INCOMING_WINDOW_SECONDS:
            recent_incoming.append(peer_id)

    if len(recent_incoming) == 1:
        return recent_incoming[0]

    if recent_incoming:
        raise VK.VkPublishError(
            "More than one recently active writable user dialog is available. "
            "Set VK_IMAGE_PEER_ID explicitly to avoid choosing the wrong person."
        )

    raise VK.VkPublishError(
        "More than one writable user dialog is available and none has a unique recent incoming message. "
        "Send the community a new private message or set VK_IMAGE_PEER_ID explicitly."
    )


def _resolve_peer_id(token: str) -> int:
    explicit = _explicit_peer_id()
    if explicit is not None:
        return explicit
    return _discover_single_writable_user_peer(token)


def _message_photo_with_peer(path: Path, token: str) -> str:
    peer_id = _resolve_peer_id(token)

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

    uploaded = VK._upload_image_bytes(upload_url, path, field_name="photo")
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
