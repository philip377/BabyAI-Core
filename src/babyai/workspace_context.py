from __future__ import annotations

from dataclasses import dataclass

from .memory import MemoryKind
from .primus import Primus
from .workspace import WorkspaceRecord


@dataclass(slots=True)
class WorkspacePrimus(Primus):
    """PRIMUS with an explicit active workspace boundary."""

    workspace: WorkspaceRecord | None = None

    def _base_prompt(self, user_input: str, *, include_tool_catalog: bool | None = None) -> str:
        base = super()._base_prompt(user_input, include_tool_catalog=include_tool_catalog)
        if self.workspace is None:
            return base

        workspace_context = self.workspace.as_context()
        extras = [workspace_context]
        task = self.working_memory.load() if self.working_memory is not None else None

        # When a workspace has no active task, the base PRIMUS prompt would not
        # include project memory at all. Keep workspace memory available without
        # inventing a fake task.
        if task is None or task.project != self.workspace.name:
            lines = [
                f"- {item.content}"
                for item in self.memory.recent(
                    limit=20,
                    kind=MemoryKind.PROJECT,
                    scope=self.workspace.name,
                )
                if self._safe_memory_content(item.content)
            ]
            remaining = max(
                self.max_context_chars - len(base) - len(workspace_context) - 4,
                0,
            )
            chunk = self._fit_section("Workspace memory:", lines, remaining)
            if chunk:
                extras.append(chunk)

        marker = f"USER: {user_input}"
        insertion = "\n\n".join([*extras, marker])
        if marker in base:
            return base.replace(marker, insertion, 1)
        return base + "\n\n" + "\n\n".join(extras)
