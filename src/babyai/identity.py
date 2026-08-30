from __future__ import annotations

import json
import re
from dataclasses import asdict, dataclass, fields
from pathlib import Path


@dataclass(frozen=True, slots=True)
class Identity:
    name: str = "BabyAI"
    owner: str = "owner"
    purpose: str = "Learn and develop alongside the owner."
    version: str = "0.1"

    def _creator_label(self) -> str:
        owner = self.owner.strip()
        if not owner or owner.casefold() == "owner":
            return "the owner/developer of the BabyAI Core project"
        return f"{owner}, the owner/developer of the BabyAI Core project"

    def system_context(self) -> str:
        return (
            f"You are {self.name} v{self.version}, a personal AI developing alongside "
            f"{self.owner}. Your purpose is: {self.purpose} "
            f"You were created by {self._creator_label()}. BabyAI itself is not a product "
            "of Anthropic, OpenAI, Google, or another AI vendor unless the configured identity "
            "explicitly says otherwise; an underlying language model may come from a separate vendor. "
            "Respond in the language of the user's latest message. Do not translate, "
            "repeat, or duplicate the answer in another language unless the user explicitly "
            "asks for a translation or a bilingual response. "
            "Preserve continuity, be curious, state uncertainty, and never perform "
            "sensitive external actions without explicit permission."
        )

    def provenance_reply(self, user_input: str) -> str | None:
        """Answer creator/origin questions deterministically instead of guessing a vendor."""

        text = re.sub(r"\s+", " ", user_input.casefold()).strip()
        markers = (
            "кем разработан",
            "кто тебя разработал",
            "кто тебя создал",
            "кем ты создан",
            "как ты создан",
            "кто твой создатель",
            "who developed you",
            "who created you",
            "who made you",
            "how were you created",
        )
        if not any(marker in text for marker in markers):
            return None

        owner = self.owner.strip()
        russian = re.search(r"[а-яё]", text) is not None
        if russian:
            creator = (
                "мой владелец/разработчик"
                if not owner or owner.casefold() == "owner"
                else owner
            )
            return (
                f"Меня разрабатывает {creator} в рамках проекта BabyAI Core. "
                "Архитектура BabyAI Core объединяет языковую модель, память, локальные инструменты "
                "и интерфейс. Сам BabyAI не является продуктом Anthropic, OpenAI или Google; "
                "конкретная подключённая языковая модель может иметь отдельного разработчика."
            )

        creator = (
            "my owner/developer"
            if not owner or owner.casefold() == "owner"
            else owner
        )
        return (
            f"I am being developed by {creator} as part of the BabyAI Core project. "
            "BabyAI Core combines a language model, memory, local tools, and the desktop interface. "
            "BabyAI itself is not an Anthropic, OpenAI, or Google product; the configured language "
            "model can have its own separate developer."
        )


class IdentityStore:
    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)

    def load_or_create(self, default: Identity) -> Identity:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if not self.path.exists():
            self.save(default)
            return default

        data = json.loads(self.path.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            raise ValueError("Identity state must be a JSON object")

        # Keep identity files forward/backward compatible across desktop releases.
        # Unknown keys from older/newer schemas must not crash the whole worker.
        allowed = {field.name for field in fields(Identity)}
        compatible = {key: value for key, value in data.items() if key in allowed}
        return Identity(**compatible)

    def save(self, identity: Identity) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(identity), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
