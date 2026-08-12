# BabyAI Desktop Orb

WinUI 3 shell for the local BabyAI Core.

## Current shell

- 132x132 borderless always-on-top window
- Desktop Acrylic backdrop
- visual states: idle, listening, thinking, approval, error
- local process bridge through `babyai-desktop exec`
- no direct SQLite or state-file access

## Development prerequisites

- Windows 10 1809+ (Windows 11 recommended)
- .NET 10 SDK
- Visual Studio 2026 with WinUI application development workload, or WinUI CLI templates
- BabyAI Core installed so `babyai-desktop` is available on PATH

## Run

```powershell
cd desktop/BabyAI.Desktop
dotnet restore
dotnet run
```

This is Shell v1. Chat expansion, drag positioning, tray integration, animations, and voice input come in later increments.
