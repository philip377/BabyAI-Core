from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

from .agent_runtime import AgentRuntime
from .primus import Primus
from .workspace_context import WorkspacePrimus


class AgentRuntimePrimusMixin:
    """Route model-selected tool calls through AgentRuntime instead of answering in host code."""

    agent_runtime: AgentRuntime | None

    def _base_prompt(self, user_input: str, *, include_tool_catalog: bool | None = None) -> str:
        base = super()._base_prompt(  # type: ignore[misc]
            user_input,
            include_tool_catalog=include_tool_catalog,
        )
        runtime = self.agent_runtime
        if runtime is None:
            return base

        observation = runtime.observation_context()
        if not observation:
            return base

        marker = f"USER: {user_input}"
        if marker in base:
            return base.replace(marker, observation + "\n\n" + marker, 1)
        return base + "\n\n" + observation

    @staticmethod
    def _agent_followup(base: str) -> str:
        return (
            base
            + "\n\nThe agent has completed the requested local action. "
            "Answer the user's latest message using the recent trusted agent observation. "
            "Do not expose internal tool names, JSON, protocol details, or permission mechanics. "
            "Do not invent local machine facts that are absent from the observation."
        )

    def _execute_or_request_approval(
        self,
        base: str,
        user_input: str,
        call,
        *,
        on_state: Callable[[str], None] | None = None,
    ) -> str:
        del base
        runtime = self.agent_runtime
        if runtime is None:
            return super()._execute_or_request_approval(  # type: ignore[misc]
                "",
                user_input,
                call,
                on_state=on_state,
            )

        def show_activity(text: str) -> None:
            if on_state is not None:
                on_state("executing")
                on_state("activity:" + text)

        result = runtime.invoke(
            user_input,
            call,
            on_activity=show_activity,
        )
        if result.approval_required:
            assert result.approval_prompt is not None
            return result.approval_prompt

        assert result.observation is not None
        followup_base = self._base_prompt(user_input, include_tool_catalog=False)
        return self.llm.generate(self._agent_followup(followup_base))  # type: ignore[attr-defined]

    def approve_pending_tool(self) -> str:
        runtime = self.agent_runtime
        if runtime is None:
            return super().approve_pending_tool()  # type: ignore[misc]

        user_input, _ = runtime.approve_pending()
        base = self._base_prompt(user_input, include_tool_catalog=False)
        response = self.llm.generate(self._agent_followup(base))  # type: ignore[attr-defined]
        self._remember_episode("babyai", response)  # type: ignore[attr-defined]
        return response

    def reject_pending_tool(self) -> str:
        runtime = self.agent_runtime
        if runtime is None:
            return super().reject_pending_tool()  # type: ignore[misc]

        runtime.reject_pending()
        response = "Доступ не предоставлен. Я не выполнял это действие."
        self._remember_episode("babyai", response)  # type: ignore[attr-defined]
        return response


@dataclass(slots=True)
class AgentPrimus(AgentRuntimePrimusMixin, Primus):
    agent_runtime: AgentRuntime | None = None


@dataclass(slots=True)
class AgentWorkspacePrimus(AgentRuntimePrimusMixin, WorkspacePrimus):
    agent_runtime: AgentRuntime | None = None
