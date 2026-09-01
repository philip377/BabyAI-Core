# VK publishing bridge

This is a deliberately small publishing bridge for the UNIX / BabyAI Core development community.

The bridge publishes text, already-uploaded VK/link attachments, and bounded local image files through the VK community wall API. It does not store a VK token in the repository and does not expose the token to post payloads.

## One-time repository setup

Create two GitHub Actions repository secrets:

- `VK_ACCESS_TOKEN` — a VK community access token with permission to publish to the community wall and upload wall photos.
- `VK_GROUP_ID` — the numeric community ID. It may be entered with or without a leading minus sign; the publisher always normalizes it to a negative `owner_id` for a community wall.

GitHub path:

`Repository -> Settings -> Secrets and variables -> Actions -> New repository secret`

Do not paste the VK access token into an issue, pull request, post JSON file, chat message, source file, or commit.

## VK-side setup

In the target VK community, create an API access key/token that is allowed to work with the community wall and photos. Keep that value private and copy it only into the `VK_ACCESS_TOKEN` GitHub Actions secret.

The bridge currently targets VK API `5.199`.

## Publishing model

A new file matching:

`social/vk/outbox/*.post.json`

on `main` triggers `.github/workflows/vk-publish.yml`.

Text-only example:

```json
{
  "message": "UNIX devlog: первый публичный тест публикации через наш VK-мост.",
  "guid": "unix-vk-first-test"
}
```

The `guid` is passed to VK as an idempotency identifier. Reusing the same `guid` helps avoid duplicate publication if the same post is retried.

## Local images

Images for automatic upload must be committed under:

`social/vk/assets/`

Example post:

```json
{
  "message": "UNIX devlog: теперь наш VK-мост умеет публиковать изображения.",
  "guid": "unix-vk-image-test",
  "image_paths": [
    "social/vk/assets/unix-avatar.png"
  ]
}
```

The publisher uses the normal VK wall-photo flow:

1. `photos.getWallUploadServer`
2. multipart upload to the returned upload URL
3. `photos.saveWallPhoto`
4. attach the returned `photo<owner_id>_<id>` to `wall.post`

Image safety rules:

- paths must remain inside `social/vk/assets/`
- absolute paths and `..` traversal are rejected
- supported extensions: `.png`, `.jpg`, `.jpeg`, `.webp`
- maximum 10 local images per post
- referenced files must exist before the VK API call begins

A post may contain only images, or combine images with text and existing `attachments`.

## Other optional fields

```json
{
  "message": "Текст поста",
  "guid": "stable-unique-id",
  "attachments": ["photo-123_456", "https://example.com"],
  "image_paths": ["social/vk/assets/unix-cover.jpg"],
  "publish_date": 1788296400,
  "close_comments": false,
  "signed": false
}
```

`publish_date` is a Unix timestamp. Existing VK attachment identifiers and supported external links can still be passed through `attachments`.

## Safety boundary

- No VK token is committed to Git.
- The workflow has read-only repository permissions.
- Only new/modified post files under the dedicated outbox path trigger automatic publication.
- Local files are uploadable only from the bounded `social/vk/assets/` directory.
- The publisher exits on validation, upload, or VK API errors instead of pretending that a post was published.
- Successful runs print the resulting VK wall URL into the GitHub Actions log.

## Manual test

The workflow also supports `workflow_dispatch` with a repository path to an existing `social/vk/outbox/*.post.json` file.

For the normal ChatGPT-assisted flow, the assistant can add an image asset plus a new immutable post file through the connected GitHub repository. The merge to `main` becomes the publication trigger, while the VK token remains private in repository secrets.
