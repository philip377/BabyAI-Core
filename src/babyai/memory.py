from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Protocol


@dataclass(slots=True)
class MemoryRecord:
    content: str
    created_at: datetime


class MemoryStore(Protocol):
    def add(self, content: str) -> MemoryRecord: ...
    def recent(self, limit: int = 10) -> list[MemoryRecord]: ...


class InMemoryStore:
    def __init__(self) -> None:
        self._items: list[MemoryRecord] = []

    def add(self, content: str) -> MemoryRecord:
        record = MemoryRecord(content=content, created_at=datetime.now(timezone.utc))
        self._items.append(record)
        return record

    def recent(self, limit: int = 10) -> list[MemoryRecord]:
        return self._items[-limit:]
