# Windows 10 XAML compatibility fallback

A real Windows 10 22H2 (build 19045) installer smoke exposed a startup-only `Microsoft.UI.Xaml.Markup.XamlParseException` while constructing `MainWindow`.

BabyAI Desktop now treats `MainWindow` XAML loading as a recoverable compatibility boundary. If the primary XAML tree cannot be created, the process stays alive and opens a minimal code-only window that points to `%LOCALAPPDATA%\BabyAI\logs\desktop-startup.log`.

This fallback is intentionally small and dependency-light. It prevents silent process termination while the exact Windows 10-incompatible XAML element is isolated. Other startup exceptions remain fatal and continue through `StartupDiagnostics.ShowFatal`.
