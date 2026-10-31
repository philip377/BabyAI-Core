"""Supervised linking contract. No production input adapter is enabled.

Only trusted operator code may supply confirm/authorize callbacks. Never expose
them as model tools. A native adapter must check identity again atomically with
its targeted UI operation; coordinate-based clicks cannot satisfy this contract.
"""
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import json
import os
from pathlib import Path
from threading import Lock
from typing import Callable, Protocol
from uuid import uuid4, UUID


class GingerGuardError(RuntimeError):
    pass


@dataclass(frozen=True)
class Selection:
    window: int
    process: int
    document: str
    supplier_row: str
    supplier_title: str
    candidate_row: str
    candidate_title: str
    linked_product: str
    verified: bool

    def require_verified(self) -> None:
        if (self.verified is not True or type(self.window) is not int
                or type(self.process) is not int or self.window <= 0 or self.process <= 0
                or any(not isinstance(value, str) or not value.strip() for value in (
                    self.document, self.supplier_row, self.supplier_title,
                    self.candidate_row, self.candidate_title))
                or not isinstance(self.linked_product, str)):
            raise GingerGuardError("Выделенная строка и кандидат не подтверждены")


class GingerAdapter(Protocol):
    def read_selection(self) -> Selection:
        """Fresh native read, verifying foreground executable and unique selection."""

    def link_if_unchanged(self, expected: Selection) -> None:
        """Recheck expected state at dispatch; invoke identified UI control once."""


class GingerOperationJournal:
    """Local, durable intent marker. All drivers must share one fixed app directory.

    An exclusive file prevents concurrent dispatch across instances. Crashes and
    partial writes leave the marker in place. No automatic recovery/clear API is
    exposed; unresolved operations require a future trusted reconciliation flow.
    This stores local JSON only, never a Ginger database connection.
    """

    def __init__(self, directory: Path):
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.pending = self.directory / "pending.json"

    def has_pending(self) -> bool:
        return self.pending.exists()

    @staticmethod
    def _write_exclusive(path: Path, data: dict) -> None:
        with path.open("x", encoding="utf-8") as stream:
            json.dump(data, stream, ensure_ascii=False, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())

    def begin(self, selection: Selection) -> str:
        selection.require_verified()
        operation_id = str(uuid4())
        try:
            self._write_exclusive(self.pending, {
                "version": 1, "operation_id": operation_id,
                "created_utc": datetime.now(timezone.utc).isoformat(),
                "selection": asdict(selection), "state": "pending",
            })
        except FileExistsError as exc:
            raise GingerGuardError("Есть незавершённая операция; повтор запрещён") from exc
        return operation_id

    def complete(self, operation_id: str, observed: Selection) -> None:
        observed.require_verified()
        if str(UUID(operation_id)) != operation_id:
            raise GingerGuardError("Неверный идентификатор операции")
        record = json.loads(self.pending.read_text(encoding="utf-8"))
        if record.get("version") != 1 or record.get("operation_id") != operation_id or record.get("state") != "pending":
            raise GingerGuardError("Журнал операции изменился; нужна ручная сверка")
        expected = Selection(**record["selection"])
        expected.require_verified()
        if (observed.window, observed.process, observed.document, observed.supplier_row,
            observed.supplier_title, observed.linked_product) != (
            expected.window, expected.process, expected.document, expected.supplier_row,
            expected.supplier_title, expected.candidate_row):
            raise GingerGuardError("Результат не соответствует сохранённому намерению")
        record.update(state="verified", result=asdict(observed),
                      verified_utc=datetime.now(timezone.utc).isoformat())
        self._write_exclusive(self.directory / f"verified-{operation_id}.json", record)
        # A crash before removal conservatively leaves the operation blocked.
        self.pending.unlink()


class GuardedGingerDriver:
    """One attempt per explicit operator confirmation; ambiguous writes latch stop.

    authorize must enforce the host's permissions and incident restrictions on
    every call. An OCR or model assertion alone cannot set verified=True.
    """

    def __init__(self, adapter: GingerAdapter, *, authorize: Callable[[], None],
                 confirm: Callable[[Selection], bool], journal: Callable[[str], None],
                 operations: GingerOperationJournal):
        self.adapter = adapter
        self.authorize = authorize
        self.confirm = confirm
        self.journal = journal
        self.operations = operations
        self.blocked = False
        self._lock = Lock()

    def link(self) -> Selection:
        if not self._lock.acquire(blocking=False):
            raise GingerGuardError("Другая операция Ginger ещё выполняется")
        try:
            if self.blocked or self.operations.has_pending():
                raise GingerGuardError("Нужна ручная сверка результата предыдущего действия")
            self.authorize()
            before = self.adapter.read_selection()
            before.require_verified()
            if before.linked_product:
                raise GingerGuardError("Строка уже сопоставлена; замена запрещена")
            self.journal("Ожидание подтверждения конкретной строки и товара")
            if self.confirm(before) is not True:
                raise GingerGuardError("Оператор не подтвердил сопоставление")
            self.authorize()
            current = self.adapter.read_selection()
            current.require_verified()
            if current != before:
                raise GingerGuardError("После подтверждения окно или строка изменились")
            # Persist intent before dispatch. Failure to log prevents the action.
            self.journal("Подтверждение получено; строка повторно проверена")
            operation_id = self.operations.begin(before)
            self.blocked = True
            try:
                self.adapter.link_if_unchanged(before)
                after = self.adapter.read_selection()
                after.require_verified()
                if (after.window, after.process, after.document, after.supplier_row,
                    after.supplier_title, after.linked_product) != (
                    before.window, before.process, before.document, before.supplier_row,
                    before.supplier_title, before.candidate_row):
                    raise GingerGuardError("Результат сопоставления не подтверждён")
                self.journal("Поле Товар проверено: сопоставление подтверждено")
                self.operations.complete(operation_id, after)
            except Exception:
                # No retries, rollback clicks or further writes after uncertainty.
                raise
            self.blocked = False
            return after
        finally:
            self._lock.release()
