# VK publishing bridge

This is a deliberately small publishing bridge for the UNIX / BabyAI Core development community.

The first version publishes text posts (and already-uploaded VK/link attachments) through `wall.post`. It does not store a VK token in the repository and does not expose the token to post payloads.

## One-time repository setup

Create two GitHub Actions repository secrets:

- `VK_ACCESS_TOKEN` — a VK community access token with permission to publish to the community wall.
- `VK_GROUP_ID` — the numeric community ID. It may be entered with or without a leading minus sign; the publisher always normalizes it to a negative `owner_id` for a community wall.

GitHub path:

`Repository -> Settings -> Secrets and variables -> Actions -> New repository secret`

Do not paste the VK access token into an issue, pull request, post JSON file, chat message, source file, or commit.

## VK-side setup

In the target VK community, create an API access key/token that is allowed to work with the community wall. Keep that value private and copy it only into the `VK_ACCESS_TOKEN` GitHub Actions secret.

The bridge currently targets VK API `5.199`.

## Publishing model

After this integration is merged, a new file matching:

`social/vk/outbox/*.post.json`

on `main` triggers `.github/workflows/vk-publish.yml`.

Example:

```json
{
  "message": "UNIX devlog: первый публичный тест публикации через наш VK-мост.",
  "guid": "unix-vk-first-test"
}
```

The `guid` is passed to VK as an idempotency identifier. Reusing the same `guid` helps avoid duplicate publication if the same post is retried.

Optional fields:

```json
{
  "message": "Текст поста",
  "guid": "stable-unique-id",
  "attachments": ["photo-123_456", "https://example.com"],
  "publish_date": 1788296400,
  "close_comments": false,
  "signed": false
}
```

`publish_date` is a Unix timestamp. `attachments` are VK attachment identifiers or a supported external link; this first slice does not upload local image files to VK yet.

## Safety boundary

- No VK token is committed to Git.
- The workflow has read-only repository permissions.
- Only post files added/modified under the dedicated outbox path trigger automatic publication.
- The integration PR itself contains no `*.post.json`, so merging the bridge does not publish anything.
- The publisher exits on VK API errors instead of pretending that a post was published.
- Successful runs print the resulting VK wall URL into the GitHub Actions log.

## Manual test

The workflow also supports `workflow_dispatch` with a repository path to an existing `social/vk/outbox/*.post.json` file.

For the normal ChatGPT-assisted flow, a new immutable post file can be committed to `main`; that push is the publication trigger. This lets the assistant prepare and submit a post through the connected GitHub repository without receiving the VK token itself.

## Next slice

After the first text post succeeds, add a bounded image-upload path using VK's wall photo upload flow rather than putting image bytes or credentials into post JSON.
