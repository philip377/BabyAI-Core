# UNIX icon

`Unix.svg` is the original, font-free vector source: an open U orbit and a detached signal point on a violet-blue orb. `Unix.png` is the 512 px preview; `Unix.ico` contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 px images. Small ICO images use 32-bit DIBs with alpha and transparency masks; 128/256 px images use PNG.

The committed assets are used directly by Desktop and Setup builds. Node, sharp and Python are not build or runtime dependencies. To deliberately regenerate assets in an existing Node development environment with `sharp` available:

```powershell
node scripts/windows/generate-unix-icons.mjs
node scripts/windows/generate-unix-icons.mjs --check
```

`--check` only compares the rendered bytes to the committed files. `BABYAI_SHARP_MODULE` can select an existing sharp module by its absolute path. The committed images were rendered with sharp 0.35.5, density 384 and Lanczos3 resizing; preserve the tool version when checking byte-for-byte reproducibility.

Desktop embeds the ICO and copies it to `Assets/Unix.ico` for the app window and notification-area icon. Setup embeds the same ICO. Existing shortcut and Installed Apps registrations point to the Desktop executable's embedded icon. The single-file release builder applies it to a private copy of the SFX module before appending its configuration and payload; the source module and non-icon resources remain unchanged.
