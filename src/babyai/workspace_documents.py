from __future__ import annotations

import json
import os
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

from .permissions import PermissionStore
from .tools import Toolset


TEXT_DOCUMENT_SUFFIXES = frozenset(
    {
        ".cfg",
        ".cs",
        ".css",
        ".csv",
        ".html",
        ".ini",
        ".js",
        ".json",
        ".log",
        ".md",
        ".markdown",
        ".py",
        ".sql",
        ".toml",
        ".ts",
        ".tsv",
        ".txt",
        ".xaml",
        ".xml",
        ".yaml",
        ".yml",
    }
)


@dataclass(frozen=True, slots=True)
class WorkspaceDocument:
    id: str
    workspace_id: str
    name: str
    path: str
    size_bytes: int
    added_at: str


class WorkspaceDocumentStore:
    """Explicit document registry scoped to exactly one Workspace.

    Registering a document stores metadata only. File contents are never read by
    add/list/get/remove and registration does not grant filesystem permission.
    """

    SCHEMA_VERSION = 1
    MAX_TEXT_BYTES = 262_144

    def __init__(self, path: str | Path, workspace_id: str) -> None:
        self.path = Path(path)
        self.workspace_id = str(workspace_id).strip()
        if not self.workspace_id:
            raise ValueError("workspace_id is required")

    def list(self) -> list[WorkspaceDocument]:
        return self._load()

    def get(self, document_id: str) -> WorkspaceDocument:
        document_id = str(document_id).strip()
        if not document_id:
            raise ValueError("Document id is required")
        for document in self._load():
            if document.id == document_id:
                return document
        raise KeyError(f"Document does not exist in the active workspace: {document_id}")

    def add(
        self,
        file_path: str | Path,
        *,
        name: str | None = None,
    ) -> WorkspaceDocument:
        target = Path(file_path).expanduser().resolve()
        if not target.is_file():
            raise FileNotFoundError(str(target))

        display_name = target.name if name is None else " ".join(str(name).split()).strip()
        if not display_name:
            raise ValueError("Document name cannot be empty")
        if len(display_name) > 120:
            raise ValueError("Document name cannot exceed 120 characters")

        documents = self._load()
        target_key = self._path_key(target)
        if any(self._path_key(Path(item.path)) == target_key for item in documents):
            raise ValueError("Document is already registered in the active workspace")

        stat = target.stat()
        record = WorkspaceDocument(
            id=uuid.uuid4().hex,
            workspace_id=self.workspace_id,
            name=display_name,
            path=str(target),
            size_bytes=stat.st_size,
            added_at=datetime.now(timezone.utc).isoformat(),
        )
        documents.append(record)
        self._save(documents)
        return record

    def remove(self, document_id: str) -> WorkspaceDocument:
        document = self.get(document_id)
        documents = [item for item in self._load() if item.id != document.id]
        self._save(documents)
        return document

    def read_text(self, document_id: str, permissions: PermissionStore) -> str:
        document = self.get(document_id)
        target = Path(document.path)
        if target.suffix.casefold() not in TEXT_DOCUMENT_SUFFIXES:
            raise ValueError(
                "Document is registered, but this foundation only reads text-based file types"
            )
        # Reuse the existing bounded filesystem.read capability. The registry only
        # narrows which path is selected; it never bypasses the permission model.
        return Toolset(permissions).read_text(target, max_bytes=self.MAX_TEXT_BYTES)

    @staticmethod
    def _path_key(path: Path) -> str:
        return os.path.normcase(str(path.resolve()))

    def _load(self) -> list[WorkspaceDocument]:
        if not self.path.exists():
            return []
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValueError("Workspace document registry is unreadable") from exc

        if not isinstance(raw, dict) or raw.get("schema_version") != self.SCHEMA_VERSION:
            raise ValueError("Unsupported workspace document registry schema")
        if raw.get("workspace_id") != self.workspace_id:
            raise ValueError("Workspace document registry belongs to another workspace")

        items = raw.get("documents", [])
        if not isinstance(items, list):
            raise ValueError("Workspace document registry is invalid")

        documents: list[WorkspaceDocument] = []
        seen_ids: set[str] = set()
        seen_paths: set[str] = set()
        for item in items:
            if not isinstance(item, dict):
                raise ValueError("Workspace document registry is invalid")
            try:
                record = WorkspaceDocument(
                    id=str(item["id"]).strip(),
                    workspace_id=str(item["workspace_id"]).strip(),
                    name=str(item["name"]).strip(),
                    path=str(item["path"]).strip(),
                    size_bytes=int(item["size_bytes"]),
                    added_at=str(item["added_at"]),
                )
            except (KeyError, TypeError, ValueError) as exc:
                raise ValueError("Workspace document registry is invalid") from exc

            if (
                not record.id
                or record.workspace_id != self.workspace_id
                or not record.name
                or not record.path
                or record.size_bytes < 0
            ):
                raise ValueError("Workspace document registry is invalid")
            path_key = self._path_key(Path(record.path))
            if record.id in seen_ids or path_key in seen_paths:
                raise ValueError("Workspace document registry contains duplicate documents")
            seen_ids.add(record.id)
            seen_paths.add(path_key)
            documents.append(record)
        return documents

    def _save(self, documents: list[WorkspaceDocument]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": self.SCHEMA_VERSION,
            "workspace_id": self.workspace_id,
            "documents": [asdict(item) for item in documents],
        }
        temporary = self.path.with_name(self.path.name + ".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self.path)
