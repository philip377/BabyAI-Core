from pathlib import Path


def test_desktop_native_chat_emits_stage_diagnostics() -> None:
    root = Path(__file__).resolve().parents[1]
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")
    commands = (root / "src" / "babyai" / "desktop_commands.py").read_text(encoding="utf-8")
    resident = (root / "src" / "babyai" / "resident_native_brain.py").read_text(encoding="utf-8")
    generation = (root / "src" / "babyai" / "native_generation.py").read_text(encoding="utf-8")

    assert 'startInfo.Environment["BABYAI_RUNTIME_LOG"] = runtimeLog;' in bridge
    assert '"native-runtime.log"' in bridge
    assert "Bridge request start:" in bridge
    assert "Bridge request cancelled:" in bridge

    assert '"chat.core.start"' in commands
    assert '"provider.native.select.done"' in commands
    assert '"native.model.open.start"' in resident
    assert '"native.model.open.done"' in resident
    assert '"native.prefill.start"' in generation
    assert '"native.prefill.done"' in generation
    assert '"native.first_token"' in generation
    assert '"native.generation.done"' in generation
    assert "tokens_per_second" in generation


def test_native_desktop_prompt_budget_is_cpu_bounded() -> None:
    root = Path(__file__).resolve().parents[1]
    commands = (root / "src" / "babyai" / "desktop_commands.py").read_text(encoding="utf-8")

    assert 'max_context_chars=6_000 if self.config.provider == "native" else 12_000' in commands


def test_native_timeout_guidance_does_not_tell_native_users_to_start_ollama() -> None:
    root = Path(__file__).resolve().parents[1]
    friendly = (
        root / "desktop" / "BabyAI.Desktop" / "FriendlyDesktopTextBehavior.cs"
    ).read_text(encoding="utf-8")

    assert 'string.Equals(provider, "native"' in friendly
    assert "Диагностика сохранена в native-runtime.log." in friendly
