from __future__ import annotations

from collections import deque

from babyai.agent_primus import AgentPrimus
from babyai.agent_desktop import AgentDesktopCommands
from babyai.agent_runtime import AgentRuntime, ModelDrivenAgentExecutor
from babyai.config import BabyAIConfig
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore, SessionMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.tool_approval import PendingToolApprovalStore


class ScriptedProvider(LLMProvider):
    def __init__(self, responses: list[str]) -> None:
        self.responses = deque(responses)
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.responses.popleft()


def build_primus(tmp_path, provider: ScriptedProvider, runtime: AgentRuntime, session=None) -> AgentPrimus:
    return AgentPrimus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=runtime.executor,
        agent_runtime=runtime,
        tool_approvals=runtime.approvals,
        repair_tool_calls=True,
        session_memory=session,
    )


def test_model_decides_to_call_agent_before_permission_handoff(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending.json")
    runtime = AgentRuntime(ModelDrivenAgentExecutor(permissions), approvals)
    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"~/Desktop"}}',
    ])
    primus = build_primus(tmp_path, provider, runtime)

    reply = primus.think("Какие файлы у меня на рабочем столе?")

    assert len(provider.prompts) == 1
    assert "Available tools:" in provider.prompts[0]
    assert "разреш" in reply.casefold()
    pending = approvals.load()
    assert pending is not None
    assert pending.tool == "filesystem.list"
    assert pending.arguments == {"path": "~/Desktop"}
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)


def test_approved_agent_observation_returns_to_llm(tmp_path) -> None:
    folder = tmp_path / "Desktop"
    folder.mkdir()
    (folder / "price.xlsx").write_text("x", encoding="utf-8")
    (folder / "notes.txt").write_text("x", encoding="utf-8")

    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending.json")
    runtime = AgentRuntime(ModelDrivenAgentExecutor(permissions), approvals)
    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"%s"}}' % folder.as_posix(),
        "В этой папке вижу price.xlsx и notes.txt.",
    ])
    primus = build_primus(tmp_path, provider, runtime)

    first = primus.think("Какие файлы находятся в этой папке?")
    assert "разреш" in first.casefold()

    final = primus.approve_pending_tool()

    assert final.startswith("Проверяю папку ")
    assert "price.xlsx" in final
    assert "notes.txt" in final
    assert "Recent trusted agent observation" in provider.prompts[-1]
    assert "OBSERVATION:" in provider.prompts[-1]
    assert "price.xlsx" in provider.prompts[-1]
    assert "notes.txt" in provider.prompts[-1]
    assert provider.prompts[-1].rstrip().endswith(
        "USER: Какие файлы находятся в этой папке?"
    )
    assert "The agent has completed the requested local action" not in provider.prompts[-1]
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)


def test_agent_observation_survives_new_primus_for_followup(tmp_path) -> None:
    folder = tmp_path / "Desktop"
    folder.mkdir()
    (folder / "first.txt").write_text("x", encoding="utf-8")
    (folder / "second.txt").write_text("x", encoding="utf-8")

    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.FILESYSTEM_LIST)
    approvals = PendingToolApprovalStore(tmp_path / "pending.json")
    runtime = AgentRuntime(ModelDrivenAgentExecutor(permissions), approvals)
    session = SessionMemoryStore(max_records=48)
    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"%s"}}' % folder.as_posix(),
        "Вижу first.txt и second.txt.",
        "Ещё там есть second.txt.",
    ])

    first_primus = build_primus(tmp_path, provider, runtime, session)
    first = first_primus.think("Какие файлы находятся в этой папке?")
    assert "first.txt" in first

    # Desktop recreates Primus each turn, while AgentRuntime stays alive with the worker.
    second_primus = build_primus(tmp_path, provider, runtime, session)
    followup = second_primus.think("а ещё какие?")

    assert followup == "Ещё там есть second.txt."
    assert "Recent trusted agent observation" in provider.prompts[-1]
    assert "first.txt" in provider.prompts[-1]
    assert "second.txt" in provider.prompts[-1]


def test_model_driven_executor_disables_old_pre_llm_shortcut(tmp_path) -> None:
    executor = ModelDrivenAgentExecutor(PermissionStore(tmp_path / "permissions.json"))

    assert executor.infer_safe_local_intent("Назови файл на моём рабочем столе") is None
    assert executor.requests_local_action("Назови файл на моём рабочем столе") is True


def test_desktop_worker_surface_runs_approved_observation_loop_and_followup(
    tmp_path,
    monkeypatch,
) -> None:
    desktop = tmp_path / "real-desktop"
    desktop.mkdir()
    (desktop / "owner-notes.txt").write_text("x", encoding="utf-8")
    (desktop / "vacation-photo.jpg").write_text("x", encoding="utf-8")
    monkeypatch.setattr(
        "babyai.tools._resolve_local_path",
        lambda path: desktop if str(path) == "~/Desktop" else path,
    )

    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"~/Desktop"}}',
        "На рабочем столе вижу owner-notes.txt и vacation-photo.jpg.",
        "Ещё там есть vacation-photo.jpg.",
    ])
    commands = AgentDesktopCommands(
        BabyAIConfig(data_dir=tmp_path / "data", provider="native"),
        persistent=True,
    )
    commands._provider_instance = provider

    events: list[dict[str, object]] = []
    first = commands.stream_chat(
        {"message": "посмотри какие файлы на рабочем столе у меня есть"},
        events.append,
    )

    assert "разреш" in first["reply"].casefold()
    assert len(provider.prompts) == 1
    assert provider.prompts[0].rstrip().endswith(
        "USER: посмотри какие файлы на рабочем столе у меня есть"
    )
    assert "Available tools:" in provider.prompts[0]

    approved = commands.execute("approval.approve")

    assert approved["activity"] == "Проверяю рабочий стол…"
    assert "owner-notes.txt" in approved["reply"]
    assert "vacation-photo.jpg" in approved["reply"]
    assert "OBSERVATION:" in provider.prompts[1]
    assert "owner-notes.txt" in provider.prompts[1]
    assert "vacation-photo.jpg" in provider.prompts[1]

    followup_events: list[dict[str, object]] = []
    followup = commands.stream_chat({"message": "а ещё какие?"}, followup_events.append)

    assert followup["reply"] == "Ещё там есть vacation-photo.jpg."
    assert "OBSERVATION:" in provider.prompts[2]
    assert "owner-notes.txt" in provider.prompts[2]
    assert "vacation-photo.jpg" in provider.prompts[2]
