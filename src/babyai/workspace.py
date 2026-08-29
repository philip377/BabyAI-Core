from __future__ import annotations

import json
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True, slots=True)
class WorkspaceRecord:
    id: str
    name: str
    root: str | None
    created_at: str

    def as_context(self) -> str:
        parts = ["Active workspace:", f"Name: {self.name}"]
        if self.root:
            parts.append(f"Root: {self.root}")
        parts.append(
            "Workspace metadata does not grant filesystem access; file contents still require an explicit capability."
        )
        return "\n".join(parts)


class WorkspaceStore:
    """Small persistent registry for user-selected project/workspace context.

    A workspace root is metadata only. This store never enumerates or reads files under
    that root and does not grant any filesystem capability.
    """

    SCHEMA_VERSION = 1

    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)

    def list(self) -> list[WorkspaceRecord]:
        workspaces, _ = self._load()
        return workspaces

    def active(self) -> WorkspaceRecord | None:
        workspaces, active_id = self._load()
        if active_id is None:
            return None
        for workspace in workspaces:
            if workspace.id == active_id:
                return workspace
        raise ValueError("Workspace state references an unknown active workspace")

    def create(self, name: str, *, root: str | Path | None = None) -> WorkspaceRecord:
        name = " ".join(str(name).split()).strip()
        if not name:
            raise ValueError("Workspace name cannot be empty")
        if len(name) > 80:
            raise ValueError("Workspace name cannot exceed 80 characters")

        workspaces, active_id = self._load()
        if any(item.name.casefold() == name.casefold() for item in workspaces):
            raise ValueError(f"Workspace already exists: {name}")

        resolved_root: str | None = None
        if root is not None and str(root).strip():
            candidate = Path(root).expanduser().resolve()
            if not candidate.exists():
                raise FileNotFoundError(str(candidate))
            if not candidate.is_dir():
                raise NotADirectoryError(str(candidate))
            resolved_root = str(candidate)

        record = WorkspaceRecord(
            id=uuid.uuid4().hex,
            name=name,
            root=resolved_root,
            created_at=datetime.now(timezone.utc).isoformat(),
        )
        workspaces.append(record)
        self._save(workspaces, active_id)
        return record

    def select(self, identifier: str) -> WorkspaceRecord:
        workspaces, _ = self._load()
        workspace = self._resolve(workspaces, identifier)
        self._save(workspaces, workspace.id)
        return workspace

    def clear_active(self) -> None:
        workspaces, _ = self._load()
        self._save(workspaces, None)

    @staticmethod
    def _resolve(workspaces: list[WorkspaceRecord], identifier: str) -> WorkspaceRecord:
        identifier = str(identifier).strip()
        if not identifier:
            raise ValueError("Workspace id or name is required")
        folded = identifier.casefold()
        for workspace in workspaces:
            if workspace.id == identifier or workspace.name.casefold() == folded:
                return workspace
        raise KeyError(f"Workspace does not exist: {identifier}")

    def _load(self) -> tuple[list[WorkspaceRecord], str | None]:
        if not self.path.exists():
            return [], None
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValueError("Workspace state is unreadable") from exc
        if not isinstance(raw, dict) or raw.get("schema_version") != self.SCHEMA_VERSION:
            raise ValueError("Unsupported workspace state schema")
        items = raw.get("workspaces", [])
        active_id = raw.get("active_id")
        if not isinstance(items, list) or (active_id is not None and not isinstance(active_id, str)):
            raise ValueError("Workspace state is invalid")

        workspaces: list[WorkspaceRecord] = []
        seen_ids: set[str] = set()
        seen_names: set[str] = set()
        for item in items:
            if not isinstance(item, dict):
                raise ValueError("Workspace state is invalid")
            try:
                record = WorkspaceRecord(
                    id=str(item["id"]).strip(),
                    name=str(item["name"]).strip(),
                    root=None if item.get("root") is None else str(item["root"]),
                    created_at=str(item["created_at"]),
                )
            except KeyError as exc:
                raise ValueError("Workspace state is invalid") from exc
            if not record.id or not record.name:
                raise ValueError("Workspace state is invalid")
            folded_name = record.name.casefold()
            if record.id in seen_ids or folded_name in seen_names:
                raise ValueError("Workspace state contains duplicate workspaces")
            seen_ids.add(record.id)
            seen_names.add(folded_name)
            workspaces.append(record)
        return workspaces, active_id

    def _save(self, workspaces: list[WorkspaceRecord], active_id: str | None) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": self.SCHEMA_VERSION,
            "active_id": active_id,
            "workspaces": [asdict(item) for item in workspaces],
        }
        temporary = self.path.with_name(self.path.name + ".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self.path)
