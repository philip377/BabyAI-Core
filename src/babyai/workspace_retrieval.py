from __future__ import annotations

import hashlib
import json
import math
import re
from collections import Counter
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


_TOKEN_RE = re.compile(r"[0-9A-Za-zА-Яа-яЁё_]{2,}", re.UNICODE)
_STOPWORDS = frozenset(
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "where", "when",
        "is", "in", "of", "to", "on", "are", "was", "were",
        "как", "что", "это", "для", "или", "его", "она", "они", "там", "тут", "где",
        "когда", "какой", "какая", "какие", "про", "под", "над", "при", "без", "есть",
        "мы", "ты", "вы", "на", "по", "из", "во", "же", "не", "да",
    }
)


@dataclass(frozen=True, slots=True)
class IndexedWorkspaceDocument:
    document_id: str
    workspace_id: str
    document_name: str
    path: str
    content_sha256: str
    indexed_at: str
    chunks: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class RetrievalHit:
    document_id: str
    document_name: str
    chunk_index: int
    score: float
    text: str


class WorkspaceRetrievalStore:
    """Deterministic local lexical retrieval for one Workspace.

    The index is populated only by an explicit ingestion step. Search never opens
    the source document; it works from the persisted indexed snapshot.
    """

    SCHEMA_VERSION = 1
    CHUNK_CHARS = 900
    OVERLAP_CHARS = 120
    MAX_CHUNKS_PER_DOCUMENT = 512

    def __init__(self, path: str | Path, workspace_id: str) -> None:
        self.path = Path(path)
        self.workspace_id = str(workspace_id).strip()
        if not self.workspace_id:
            raise ValueError("workspace_id is required")

    def list(self) -> list[IndexedWorkspaceDocument]:
        return self._load()

    def ingest(
        self,
        *,
        document_id: str,
        document_name: str,
        path: str,
        content: str,
    ) -> IndexedWorkspaceDocument:
        document_id = str(document_id).strip()
        document_name = " ".join(str(document_name).split()).strip()
        source_path = str(path).strip()
        if not document_id:
            raise ValueError("document_id is required")
        if not document_name:
            raise ValueError("document_name is required")
        if not source_path:
            raise ValueError("document path is required")
        if not isinstance(content, str):
            raise ValueError("document content must be text")

        normalized = self._normalize_text(content)
        if not normalized:
            raise ValueError("Document contains no indexable text")
        chunks = tuple(self._chunk_text(normalized))
        if not chunks:
            raise ValueError("Document contains no indexable text")
        if len(chunks) > self.MAX_CHUNKS_PER_DOCUMENT:
            raise ValueError("Document produces too many retrieval chunks")

        record = IndexedWorkspaceDocument(
            document_id=document_id,
            workspace_id=self.workspace_id,
            document_name=document_name,
            path=source_path,
            content_sha256=hashlib.sha256(content.encode("utf-8")).hexdigest(),
            indexed_at=datetime.now(timezone.utc).isoformat(),
            chunks=chunks,
        )
        records = [item for item in self._load() if item.document_id != document_id]
        records.append(record)
        self._save(records)
        return record

    def remove(self, document_id: str) -> bool:
        document_id = str(document_id).strip()
        if not document_id:
            raise ValueError("document_id is required")
        records = self._load()
        remaining = [item for item in records if item.document_id != document_id]
        if len(remaining) == len(records):
            return False
        self._save(remaining)
        return True

    def status(self, *, allowed_document_ids: set[str] | None = None) -> dict[str, int]:
        records = self._allowed_records(allowed_document_ids)
        return {
            "document_count": len(records),
            "chunk_count": sum(len(item.chunks) for item in records),
        }

    def search(
        self,
        query: str,
        *,
        limit: int = 4,
        allowed_document_ids: set[str] | None = None,
    ) -> list[RetrievalHit]:
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 8:
            raise ValueError("retrieval limit must be between 1 and 8")
        query_text = " ".join(str(query).split()).strip()
        if not query_text:
            return []

        query_tokens = set(self._tokens(query_text))
        if not query_tokens:
            return []
        records = self._allowed_records(allowed_document_ids)

        candidates: list[tuple[IndexedWorkspaceDocument, int, str, Counter[str]]] = []
        document_frequency: Counter[str] = Counter()
        for record in records:
            for chunk_index, chunk in enumerate(record.chunks):
                counts = Counter(self._tokens(chunk))
                if not counts:
                    continue
                present = set(counts) & query_tokens
                if not present:
                    continue
                candidates.append((record, chunk_index, chunk, counts))
                for token in present:
                    document_frequency[token] += 1

        if not candidates:
            return []

        candidate_count = len(candidates)
        query_phrase = query_text.casefold()
        hits: list[RetrievalHit] = []
        for record, chunk_index, chunk, counts in candidates:
            score = 0.0
            for token in query_tokens:
                frequency = counts.get(token, 0)
                if not frequency:
                    continue
                inverse_frequency = math.log(
                    (candidate_count + 1) / (document_frequency[token] + 1)
                ) + 1.0
                score += inverse_frequency * (1.0 + math.log(frequency))

            title_tokens = set(self._tokens(record.document_name))
            score += 0.35 * len(query_tokens & title_tokens)
            if len(query_phrase) >= 4 and query_phrase in chunk.casefold():
                score += 2.5
            hits.append(
                RetrievalHit(
                    document_id=record.document_id,
                    document_name=record.document_name,
                    chunk_index=chunk_index,
                    score=round(score, 6),
                    text=chunk,
                )
            )

        hits.sort(
            key=lambda item: (
                -item.score,
                item.document_name.casefold(),
                item.chunk_index,
                item.document_id,
            )
        )
        return hits[:limit]

    def _allowed_records(
        self, allowed_document_ids: set[str] | None
    ) -> list[IndexedWorkspaceDocument]:
        records = self._load()
        if allowed_document_ids is None:
            return records
        return [item for item in records if item.document_id in allowed_document_ids]

    @classmethod
    def _tokens(cls, text: str) -> list[str]:
        tokens = [match.group(0).casefold() for match in _TOKEN_RE.finditer(text)]
        return [token for token in tokens if token not in _STOPWORDS]

    @staticmethod
    def _normalize_text(content: str) -> str:
        content = content.replace("\r\n", "\n").replace("\r", "\n")
        lines = [" ".join(line.split()) for line in content.split("\n")]
        paragraphs: list[str] = []
        current: list[str] = []
        for line in lines:
            if line:
                current.append(line)
                continue
            if current:
                paragraphs.append(" ".join(current))
                current.clear()
        if current:
            paragraphs.append(" ".join(current))
        return "\n\n".join(paragraphs).strip()

    @classmethod
    def _chunk_text(cls, text: str) -> Iterable[str]:
        raw_words = text.split()
        if not raw_words:
            return

        words: list[str] = []
        for word in raw_words:
            if len(word) <= cls.CHUNK_CHARS:
                words.append(word)
                continue
            words.extend(
                word[index : index + cls.CHUNK_CHARS]
                for index in range(0, len(word), cls.CHUNK_CHARS)
            )

        current: list[str] = []
        current_chars = 0
        for word in words:
            extra = len(word) + (1 if current else 0)
            if current and current_chars + extra > cls.CHUNK_CHARS:
                yield " ".join(current)
                overlap: list[str] = []
                overlap_chars = 0
                for previous in reversed(current):
                    addition = len(previous) + (1 if overlap else 0)
                    if overlap and overlap_chars + addition > cls.OVERLAP_CHARS:
                        break
                    overlap.append(previous)
                    overlap_chars += addition
                current = list(reversed(overlap))
                current_chars = len(" ".join(current))

            extra = len(word) + (1 if current else 0)
            if current and current_chars + extra > cls.CHUNK_CHARS:
                current = []
                current_chars = 0
            current.append(word)
            current_chars += len(word) + (1 if len(current) > 1 else 0)

        if current:
            yield " ".join(current)

    def _load(self) -> list[IndexedWorkspaceDocument]:
        if not self.path.exists():
            return []
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValueError("Workspace retrieval index is unreadable") from exc

        if not isinstance(raw, dict) or raw.get("schema_version") != self.SCHEMA_VERSION:
            raise ValueError("Unsupported workspace retrieval index schema")
        if raw.get("workspace_id") != self.workspace_id:
            raise ValueError("Workspace retrieval index belongs to another workspace")
        items = raw.get("documents", [])
        if not isinstance(items, list):
            raise ValueError("Workspace retrieval index is invalid")

        records: list[IndexedWorkspaceDocument] = []
        seen_ids: set[str] = set()
        for item in items:
            if not isinstance(item, dict):
                raise ValueError("Workspace retrieval index is invalid")
            try:
                chunks_raw = item["chunks"]
                if not isinstance(chunks_raw, list):
                    raise TypeError("chunks")
                record = IndexedWorkspaceDocument(
                    document_id=str(item["document_id"]).strip(),
                    workspace_id=str(item["workspace_id"]).strip(),
                    document_name=str(item["document_name"]).strip(),
                    path=str(item["path"]).strip(),
                    content_sha256=str(item["content_sha256"]).strip(),
                    indexed_at=str(item["indexed_at"]).strip(),
                    chunks=tuple(str(chunk).strip() for chunk in chunks_raw),
                )
            except (KeyError, TypeError, ValueError) as exc:
                raise ValueError("Workspace retrieval index is invalid") from exc

            if (
                not record.document_id
                or record.workspace_id != self.workspace_id
                or not record.document_name
                or not record.path
                or len(record.content_sha256) != 64
                or not record.indexed_at
                or not record.chunks
                or any(not chunk for chunk in record.chunks)
            ):
                raise ValueError("Workspace retrieval index is invalid")
            if record.document_id in seen_ids:
                raise ValueError("Workspace retrieval index contains duplicate documents")
            seen_ids.add(record.document_id)
            records.append(record)
        return records

    def _save(self, records: list[IndexedWorkspaceDocument]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": self.SCHEMA_VERSION,
            "workspace_id": self.workspace_id,
            "documents": [asdict(item) for item in records],
        }
        temporary = self.path.with_name(self.path.name + ".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self.path)
