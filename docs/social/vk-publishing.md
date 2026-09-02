# VK publishing bridge

This is a deliberately small publishing bridge for the UNIX / BabyAI Core development community.

The active publishing model is intentionally simple:

- text-only posts are published automatically with the VK community token;
- posts that contain local images are prepared but **not** published automatically;
- the owner attaches the image in VK and publishes that post manually.

This keeps normal devlog automation working without depending on the currently unreliable VK community-token photo upload path.

## One-time repository setup

Create these GitHub Actions repository secrets:

- `VK_ACCESS_TOKEN` — the VK community access token used for text/community-wall publication.
- `VK_GROUP_ID` — the numeric community ID. It may be entered with or without a leading minus sign; the publisher normalizes it to a negative `owner_id` for a community wall.

GitHub path:

`Repository -> Settings -> Secrets and variables -> Actions -> New repository secret`

Never paste the access token into an issue, pull request, post JSON file, chat message, source file, or commit.

`VK_IMAGE_ACCESS_TOKEN` and `VK_IMAGE_PEER_ID` are not required by the active workflow. Older image-upload experiments remain in repository history/code for possible future work, but they are not part of the current publishing path.

The bridge targets VK API `5.199`.

## Publishing model

A new file matching:

`social/vk/outbox/*.post.json`

on `main` triggers `.github/workflows/vk-publish.yml`.

### Text-only post

```json
{
  "message": "UNIX devlog: первый публичный тест публикации через наш VK-мост.",
  "guid": "unix-vk-first-test"
}
```

Text-only posts are published automatically with `VK_ACCESS_TOKEN`.

The `guid` is passed to VK as an idempotency identifier. Reusing the same `guid` helps avoid duplicate publication if the same post is retried.

### Post with an image

Images may be kept under:

`social/vk/assets/`

Example:

```json
{
  "message": "UNIX devlog: обновили локального ассистента и подготовили новый интерфейс.",
  "guid": "unix-vk-ui-update",
  "image_paths": [
    "social/vk/assets/unix-ui-update.jpg"
  ]
}
```

When `image_paths` is present, GitHub Actions validates the post and image paths, then stops before any VK publication call. The run prints a clear `Manual VK handoff` message.

Manual flow:

1. take the prepared post text;
2. open the UNIX community in VK;
3. create a new post;
4. paste the text;
5. attach the desired image(s);
6. publish manually.

This ensures the text is not accidentally published before the image is attached.

Image validation rules are still kept:

- paths must remain inside `social/vk/assets/`;
- absolute paths and `..` traversal are rejected;
- supported extensions: `.png`, `.jpg`, `.jpeg`, `.webp`;
- maximum 10 local images per post;
- referenced files must exist before the handoff is accepted.

## Existing VK attachments

A post can still use already-uploaded VK attachment identifiers or supported links through `attachments` without local `image_paths`:

```json
{
  "message": "Текст поста",
  "guid": "stable-unique-id",
  "attachments": ["photo-123_456", "https://example.com"]
}
```

Those are treated as a normal automatic post because no local image upload is needed.

## Other optional fields

```json
{
  "message": "Текст поста",
  "guid": "stable-unique-id",
  "publish_date": 1788296400,
  "close_comments": false,
  "signed": false
}
```

`publish_date` is a Unix timestamp.

## Safety boundary

- No VK token is committed to Git.
- The workflow has read-only repository permissions.
- Text posts use the narrower community token.
- Local-image posts fail over to manual publication instead of attempting unreliable token/photo workarounds.
- Only new/modified post files under the dedicated outbox path trigger the workflow.
- Local image paths remain bounded to `social/vk/assets/`.
- The publisher exits on validation or VK API errors instead of pretending that a post was published.
- Successful automatic runs print the resulting VK wall URL into the GitHub Actions log.

## Manual test

The workflow supports `workflow_dispatch` with a repository path to an existing `social/vk/outbox/*.post.json` file.

For the normal ChatGPT-assisted flow, the assistant can prepare the post JSON and image asset in the connected GitHub repository. Text-only posts can go live automatically after merge. Image posts are handed to the owner for the final VK attachment and publish click.
