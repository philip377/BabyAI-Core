# VK community-token image fallback

This note documents the bounded fallback used when VK rejects `photos.getWallUploadServer` for a community token with error 27.

The fallback uses `photos.getMessagesUploadServer` with `peer_id` set to the target community owner id (negative group id), uploads the image using multipart field `file`, saves it with `photos.saveMessagesPhoto`, then passes the returned photo attachment to the normal `wall.post` call.

The dedicated `VK_IMAGE_ACCESS_TOKEN` remains optional and, when present, is preferred for image posts. Without it, the workflow uses the existing community token and the bounded peer-aware fallback.

The fallback remains fail-closed: upload/save/post errors stop the workflow and no success is reported.
