"""Supervised linking contract. No production input adapter is enabled.

Only trusted operator code may supply confirm/authorize callbacks. Never expose
them as model tools. A native adapter must check identity again atomically with
its targeted UI operation; coordinate-based clicks cannot satisfy this contract.
"""
from dataclasses import dataclass
from threading import Lock
from typing import Callable, Protocol


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


class GuardedGingerDriver:
    """One attempt per explicit operator confirmation; ambiguous writes latch stop.

    authorize must enforce the host's permissions and incident restrictions on
    every call. An OCR or model assertion alone cannot set verified=True.
    """

    def __init__(self, adapter: GingerAdapter, *, authorize: Callable[[], None],
                 confirm: Callable[[Selection], bool], journal: Callable[[str], None]):
        self.adapter = adapter
        self.authorize = authorize
        self.confirm = confirm
        self.journal = journal
        self.blocked = False
        self._lock = Lock()

    def link(self) -> Selection:
        if not self._lock.acquire(blocking=False):
            raise GingerGuardError("Другая операция Ginger ещё выполняется")
        try:
            if self.blocked:
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
            except Exception:
                # No retries, rollback clicks or further writes after uncertainty.
                raise
            self.blocked = False
            return after
        finally:
            self._lock.release()
