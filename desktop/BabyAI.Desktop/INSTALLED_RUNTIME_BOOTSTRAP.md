# Installed Desktop runtime bootstrap

When the installed `BabyAI.Desktop.exe` is launched directly (for example from the Desktop or Start Menu shortcut), it reconstructs the self-contained runtime environment from its side-by-side installation layout before starting the Python worker.

The Desktop resolves `%LOCALAPPDATA%\BabyAI\versions\<version>` from its own `app` directory, uses the embedded `python\python.exe`, configures the CPU/Vulkan native runtime paths, and reads the preserved `%LOCALAPPDATA%\BabyAI\launch.json` preferences. Development builds outside this installed layout keep the existing environment/system-Python fallback.

The Performance settings page persists `acceleration` in `launch.json` and restarts only the desktop worker, so the next request can switch profiles without restarting the app. Supported profiles are `cpu` (`n_gpu_layers=0`), `vulkan` (full offload), `hybrid` (20 GPU layers with CPU fallback for the rest), and `auto` (full Vulkan when available, otherwise CPU).
