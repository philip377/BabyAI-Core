from __future__ import annotations

from dataclasses import asdict

from .desktop_commands import DesktopCommandError
from .permissions import PermissionStore
from .workspace import WorkspaceRecord
from .workspace_desktop_commands import WorkspaceDesktopCommands as _WorkspaceDesktopCommands
from .workspace_retrieval import WorkspaceRetrievalStore


class WorkspaceDesktopCommands(_WorkspaceDesktopCommands):
    """Workspace desktop surface with document retrieval wiring.

    This keeps the already-stable Workspace command implementation intact while
    exposing the retrieval foundation through the same trusted Desktop protocol.
    """

    def _retrieval_store(
        self, workspace: WorkspaceRecord | None = None
    ) -> WorkspaceRetrievalStore:
        workspace = self._active_workspace() if workspace is None else workspace
        if workspace is None:
            raise DesktopCommandError(
                "An active workspace is required for document retrieval commands"
            )
        return WorkspaceRetrievalStore(
            self.config.workspace_retrieval_dir / f"{workspace.id}.json",
            workspace.id,
        )

    def _registered_document_ids(
        self, workspace: WorkspaceRecord | None = None
    ) -> set[str]:
        workspace = self._active_workspace() if workspace is None else workspace
        if workspace is None:
            raise DesktopCommandError(
                "An active workspace is required for document retrieval commands"
            )
        return {document.id for document in self._document_store(workspace).list()}

    def _reset_retrieval_cache(self, retrieval: WorkspaceRetrievalStore) -> bool:
        try:
            retrieval.path.unlink(missing_ok=True)
        except OSError:
            return False
        return True

    def execute(
        self,
        command: str,
        payload: dict[str, object] | None = None,
    ) -> dict[str, object]:
        payload = payload or {}

        if command == "document.ingest":
            document_id = str(payload.get("id", "")).strip()
            if not document_id:
                raise DesktopCommandError("document.ingest.id is required")
            documents = self._document_store()
            retrieval = self._retrieval_store()
            try:
                document = documents.get(document_id)
                content = documents.read_text(
                    document.id,
                    PermissionStore(self.config.permissions_file),
                )
                indexed = retrieval.ingest(
                    document_id=document.id,
                    document_name=document.name,
                    path=document.path,
                    content=content,
                )
            except (
                ValueError,
                KeyError,
                FileNotFoundError,
                PermissionError,
                OSError,
            ) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "document": {
                    "id": indexed.document_id,
                    "name": indexed.document_name,
                    "chunks": len(indexed.chunks),
                    "content_sha256": indexed.content_sha256,
                    "indexed_at": indexed.indexed_at,
                },
            }

        if command == "document.search":
            query = str(payload.get("query", "")).strip()
            if not query:
                raise DesktopCommandError("document.search.query is required")
            limit = payload.get("limit", 4)
            try:
                hits = self._retrieval_store().search(
                    query,
                    limit=limit,
                    allowed_document_ids=self._registered_document_ids(),
                )
            except (ValueError, OSError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "hits": [asdict(hit) for hit in hits],
            }

        if command == "document.retrieval_status":
            try:
                state = self._retrieval_store().status(
                    allowed_document_ids=self._registered_document_ids()
                )
            except (ValueError, OSError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, **state}

        if command == "document.remove":
            workspace = self._active_workspace()
            retrieval = None if workspace is None else self._retrieval_store(workspace)
            document_id = str(payload.get("id", "")).strip()
            result = super().execute(command, payload)
            if retrieval is None:
                return result

            removed = False
            cache_reset = False
            try:
                removed = retrieval.remove(document_id)
            except (ValueError, OSError):
                # The retrieval index is derived state. A broken cache must never
                # block removal of the source document metadata. Resetting the
                # cache is safe because it can be rebuilt by explicit ingestion.
                cache_reset = self._reset_retrieval_cache(retrieval)
            result["retrieval"] = {
                "removed": removed,
                "cache_reset": cache_reset,
            }
            return result

        if command == "status":
            result = super().execute(command, payload)
            snapshot = result.get("snapshot")
            if not isinstance(snapshot, dict):
                return result
            workspace = self._active_workspace()
            if workspace is None:
                snapshot["retrieval"] = {
                    "healthy": True,
                    "document_count": 0,
                    "chunk_count": 0,
                }
                return result

            try:
                state = self._retrieval_store(workspace).status(
                    allowed_document_ids=self._registered_document_ids(workspace)
                )
            except (ValueError, OSError):
                snapshot["retrieval"] = {
                    "healthy": False,
                    "document_count": 0,
                    "chunk_count": 0,
                }
            else:
                snapshot["retrieval"] = {"healthy": True, **state}
            return result

        return super().execute(command, payload)
