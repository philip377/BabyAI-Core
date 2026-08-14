# Verified model download contract

`BabyAI.Setup` may optionally consume a `model.json` file located at the root of the verified release bundle.

The installer must not download a model unless this manifest exists and validates successfully.

Example:

```json
{
  "url": "https://example.invalid/babyai.gguf",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "size": 1234567890,
  "filename": "babyai.gguf",
  "display_name": "BabyAI Local Model"
}
```

Rules:

- `url` must use HTTPS.
- `sha256` must be exactly 64 hexadecimal characters.
- `size` must be between 64 MiB and 32 GiB.
- `filename` must be a plain `.gguf` file name without path traversal.
- downloads are written to `%LOCALAPPDATA%\BabyAI\models\<filename>.partial` first;
- streaming download is capped by the declared size;
- the final file size and SHA-256 must both match the manifest before atomic rename;
- failed or cancelled attempts remove the partial file best-effort;
- an existing model is reused only when its SHA-256 matches the manifest.

The actual production model URL and SHA-256 are intentionally not hard-coded in source. Release packaging will supply the approved manifest once a model artifact is selected and pinned.
