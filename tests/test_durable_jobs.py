from __future__ import annotations

import pytest

from babyai.agent_desktop import AgentDesktopCommands
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError
from babyai.jobs import DurableJobStore
from babyai.tool_approval import PendingToolApproval, PendingToolApprovalStore


def test_job_store_persists_identity_transitions_and_events(tmp_path):
    store = DurableJobStore(tmp_path / "jobs.sqlite3")
    created = store.create("Inspect the project", workspace_id="ws-1", chat_id="chat-1")

    assert created.status == "queued"
    assert created.workspace_id == "ws-1"
    assert created.chat_id == "chat-1"

    running = store.start(created.id)
    assert running.status == "running"
    waiting = store.wait_for_permission(created.id, checkpoint="Need permission")
    assert waiting.status == "waiting_permission"
    continued = store.continue_after_permission(created.id)
    assert continued.status == "running"
    completed = store.complete(created.id, result="Finished")
    assert completed.status == "completed"
    assert completed.result == "Finished"

    reopened = DurableJobStore(tmp_path / "jobs.sqlite3")
    assert reopened.get(created.id).result == "Finished"
    assert [event.to_status for event in reopened.events(created.id)] == [
        "queued",
        "running",
        "waiting_permission",
        "running",
        "completed",
    ]


def test_interrupted_running_job_recovers_paused_without_replay(tmp_path):
    store = DurableJobStore(tmp_path / "jobs.sqlite3")
    job = store.create("Potentially side-effecting work")
    store.start(job.id)

    recovered = DurableJobStore(tmp_path / "jobs.sqlite3").recover_interrupted()

    assert [item.id for item in recovered] == [job.id]
    paused = store.get(job.id)
    assert paused.status == "paused"
    assert "resume explicitly" in paused.checkpoint
    assert store.resume(job.id).status == "queued"


def test_cancel_and_invalid_terminal_transition_are_explicit(tmp_path):
    store = DurableJobStore(tmp_path / "jobs.sqlite3")
    job = store.create("Do later")
    cancelled = store.cancel(job.id)

    assert cancelled.status == "cancelled"
    assert store.cancel(job.id).status == "cancelled"
    with pytest.raises(ValueError):
        store.start(job.id)

    done = store.create("Complete once")
    store.start(done.id)
    store.complete(done.id, result="ok")
    with pytest.raises(ValueError, match="Completed jobs cannot be cancelled"):
        store.cancel(done.id)


def test_job_lists_are_workspace_isolated(tmp_path):
    store = DurableJobStore(tmp_path / "jobs.sqlite3")
    legacy = store.create("Legacy")
    alpha = store.create("Alpha", workspace_id="a")
    store.create("Beta", workspace_id="b")

    assert [item.id for item in store.list(workspace_id=None)] == [legacy.id]
    assert [item.id for item in store.list(workspace_id="a")] == [alpha.id]


def test_pending_approval_owner_is_persistent_and_requires_waiting_state(tmp_path):
    store = DurableJobStore(tmp_path / "jobs.sqlite3")
    job = store.create("Need one-shot permission")
    with pytest.raises(ValueError):
        store.set_pending_approval_owner(job.id)

    store.start(job.id)
    store.wait_for_permission(job.id, checkpoint="approve?")
    store.set_pending_approval_owner(job.id)

    reopened = DurableJobStore(tmp_path / "jobs.sqlite3")
    assert reopened.pending_approval_owner() == job.id
    reopened.clear_pending_approval_owner()
    assert reopened.pending_approval_owner() is None


def _commands(tmp_path) -> AgentDesktopCommands:
    return AgentDesktopCommands(BabyAIConfig(data_dir=tmp_path))


def test_desktop_job_create_run_and_list(tmp_path):
    commands = _commands(tmp_path)
    created = commands.execute("job.create", {"goal": "Summarize the current task"})["job"]

    class FakeCore:
        def think(self, message: str) -> str:
            assert message == "Summarize the current task"
            return "Task summarized"

    commands._core = lambda: FakeCore()  # type: ignore[method-assign]
    result = commands.execute("job.run", {"id": created["id"]})

    assert result["job"]["status"] == "completed"
    assert result["job"]["result"] == "Task summarized"
    listed = commands.execute("job.list", {})["jobs"]
    assert listed[0]["id"] == created["id"]


def test_desktop_job_waits_for_approval_then_completes_on_approve(tmp_path):
    commands = _commands(tmp_path)
    approval_store = PendingToolApprovalStore(commands.config.pending_tool_approval_file)
    created = commands.execute("job.create", {"goal": "Read a protected file"})["job"]

    class FakeCore:
        def think(self, message: str) -> str:
            approval_store.save(
                PendingToolApproval(
                    user_input=message,
                    tool="filesystem.read",
                    arguments={"path": "notes.txt"},
                    capability="filesystem.read",
                )
            )
            return "Permission required"

        def approve_pending_tool(self) -> str:
            assert approval_store.load() is not None
            approval_store.clear()
            return "Protected file read once"

        def reject_pending_tool(self) -> str:
            approval_store.clear()
            return "Rejected"

    commands._core = lambda: FakeCore()  # type: ignore[method-assign]
    waiting = commands.execute("job.run", {"id": created["id"]})["job"]
    assert waiting["status"] == "waiting_permission"
    assert commands._jobs.pending_approval_owner() == created["id"]

    approved = commands.execute("approval.approve", {})
    assert approved["job"]["status"] == "completed"
    assert approved["job"]["result"] == "Protected file read once"
    assert commands._jobs.pending_approval_owner() is None


def test_desktop_job_rejection_pauses_and_can_be_explicitly_resumed(tmp_path):
    commands = _commands(tmp_path)
    approval_store = PendingToolApprovalStore(commands.config.pending_tool_approval_file)
    created = commands.execute("job.create", {"goal": "Write a protected file"})["job"]

    class FakeCore:
        def think(self, message: str) -> str:
            approval_store.save(
                PendingToolApproval(
                    user_input=message,
                    tool="filesystem.write",
                    arguments={"path": "notes.txt", "content": "x"},
                    capability="filesystem.write",
                )
            )
            return "Permission required"

        def reject_pending_tool(self) -> str:
            approval_store.clear()
            return "Доступ не предоставлен. Я не выполнял это действие."

    commands._core = lambda: FakeCore()  # type: ignore[method-assign]
    commands.execute("job.run", {"id": created["id"]})
    rejected = commands.execute("approval.reject", {})

    assert rejected["job"]["status"] == "paused"
    resumed = commands.execute("job.resume", {"id": created["id"]})
    assert resumed["job"]["status"] == "queued"


def test_desktop_job_cannot_cross_workspace_boundary(tmp_path):
    commands = _commands(tmp_path)
    workspace = commands.execute("workspace.create", {"name": "Alpha"})["workspace"]
    commands.execute("workspace.select", {"id": workspace["id"]})
    job = commands.execute("job.create", {"goal": "Workspace-only work"})["job"]
    commands.execute("workspace.clear_active", {})

    with pytest.raises(DesktopCommandError, match="different workspace"):
        commands.execute("job.get", {"id": job["id"]})
