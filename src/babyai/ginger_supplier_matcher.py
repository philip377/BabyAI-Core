"""Read-only supplier-to-Ginger catalogue matching.

This module never talks to Ginger, SQL, the network, or a Windows process.
Approval of a supplier alias is separate from recommending a candidate.
"""
from __future__ import annotations

import json
import re
import unicodedata
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path


_RU_TO_LATIN = str.maketrans({
    "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e",
    "ё": "e", "ж": "zh", "з": "z", "и": "i", "й": "y", "к": "k",
    "л": "l", "м": "m", "н": "n", "о": "o", "п": "p", "р": "r",
    "с": "s", "т": "t", "у": "u", "ф": "f", "х": "kh", "ц": "ts",
    "ч": "ch", "ш": "sh", "щ": "sch", "ъ": "", "ы": "y",
    "ь": "", "э": "e", "ю": "yu", "я": "ya",
})
_VOLUME_RE = re.compile(r"(?<!\w)(\d+(?:[.,]\d+)?)\s*(?:ml|мл)\b", re.I)
_TESTER_RE = re.compile(r"\b(?:tester|testeur|тестер|тест)\b", re.I)
_SET_RE = re.compile(r"\b(?:set|gift\s+set|набор|комплект|kit)\b", re.I)
_CONCENTRATIONS = (
    ("extrait", r"\b(?:extrait(?:\s+de\s+parfum)?|экстракт\s+духов)\b"),
    ("edp", r"\b(?:eau\s+de\s+parfum|парфюмированная\s+вода|парфюмерная\s+вода|edp|пв)\b"),
    ("edt", r"\b(?:eau\s+de\s+toilette|туалетная\s+вода|edt|тв)\b"),
    ("edc", r"\b(?:eau\s+de\s+cologne|одеколон|edc)\b"),
    ("parfum", r"\b(?:pure\s+parfum|духи|parfum|perfume)\b"),
)
# Only the packaging descriptions below are discarded; flankers and product
# names (Intense, Qahwa, Elixir, etc.) must remain distinguishable.
_NOISE_RE = re.compile(
    r"\b(?:spray|спрей|парфюм|аромат|original|оригинал|жен|муж)\b", re.I
)


def normalize_text(text: str) -> str:
    text = unicodedata.normalize("NFKD", text.casefold())
    text = "".join(c for c in text if not unicodedata.combining(c))
    text = text.translate(_RU_TO_LATIN)
    return " ".join(re.findall(r"[a-z0-9]+", text))


@dataclass(frozen=True, slots=True)
class CatalogPosition:
    sku: str
    title: str
    brand: str = ""
    volume_ml: float | None = None
    concentration: str = ""
    is_tester: bool | None = None
    barcode: str = ""


@dataclass(frozen=True, slots=True)
class SupplierPosition:
    supplier_id: str
    title: str
    position_id: str = ""
    brand: str = ""
    volume_ml: float | None = None
    concentration: str = ""
    is_tester: bool | None = None
    barcode: str = ""


@dataclass(frozen=True, slots=True)
class Candidate:
    sku: str
    title: str
    score: float


@dataclass(frozen=True, slots=True)
class MatchResult:
    status: str  # known, suggested, review, unmatched
    candidates: tuple[Candidate, ...]
    matched_sku: str | None = None


@dataclass(frozen=True, slots=True)
class _Features:
    name: str
    brand: str
    volume_ml: float | None
    concentration: str
    is_tester: bool | None
    barcode: str
    is_set: bool


def _features(item: CatalogPosition | SupplierPosition, *, brand_hint: str = "") -> _Features:
    raw = item.title.casefold()
    volumes = {float(v.replace(",", ".")) for v in _VOLUME_RE.findall(raw)}
    volume = item.volume_ml if item.volume_ml is not None else (
        next(iter(volumes)) if len(volumes) == 1 else None
    )
    # Multiple distinct volumes or multipacks are ambiguous, not ordinary bottles.
    is_set = bool(_SET_RE.search(raw) or re.search(r"\b\d+\s*[xх×]\s*\d+\s*(?:мл|ml)\b", raw, re.I))
    if len(volumes) > 1:
        is_set = True

    concentration = item.concentration.casefold().strip()
    if concentration:
        aliases = {"eau de parfum": "edp", "eau de toilette": "edt",
                   "eau de cologne": "edc", "perfume": "parfum"}
        concentration = aliases.get(concentration, concentration)
    detected = ""
    for value, pattern in _CONCENTRATIONS:
        if re.search(pattern, raw, re.I):
            detected = value
            raw = re.sub(pattern, " ", raw, flags=re.I)
            break
    # A contradictory explicit label is not silently converted into a match.
    if concentration and detected and concentration != detected:
        is_set = True
    concentration = concentration or detected

    tester = item.is_tester
    if tester is None and _TESTER_RE.search(raw):
        tester = True
    raw = _TESTER_RE.sub(" ", raw)
    raw = _SET_RE.sub(" ", raw)
    raw = _VOLUME_RE.sub(" ", raw)
    raw = _NOISE_RE.sub(" ", raw)
    brand = normalize_text(item.brand)
    name = normalize_text(raw)

    for prefix in (brand, normalize_text(brand_hint)):
        if prefix and (name == prefix or name.startswith(prefix + " ")):
            name = name[len(prefix):].strip()
    return _Features(name, brand, volume, concentration, tester, item.barcode.strip(), is_set)


def _compatible(a: _Features, b: _Features) -> bool:
    if a.brand and b.brand and a.brand != b.brand:
        return False
    if a.volume_ml is not None and b.volume_ml is not None:
        if abs(a.volume_ml - b.volume_ml) > 0.001:
            return False
    if a.concentration and b.concentration and a.concentration != b.concentration:
        return False
    if a.is_tester is not None and b.is_tester is not None:
        if a.is_tester != b.is_tester:
            return False
    if a.barcode and b.barcode and a.barcode != b.barcode:
        return False
    if a.is_set != b.is_set:
        return False
    return True


def _name_score(a: str, b: str) -> float:
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    left, right = a.split(), b.split()
    def avg_best(xs: list[str], ys: list[str]) -> float:
        return sum(max(SequenceMatcher(None, x, y).ratio() for y in ys) for x in xs) / len(xs)
    token_score = (avg_best(left, right) + avg_best(right, left)) / 2
    return 0.75 * token_score + 0.25 * SequenceMatcher(None, a, b).ratio()


def _similarity(source: _Features, target: _Features) -> float:
    score = _name_score(source.name, target.name)
    if source.barcode and target.barcode and source.barcode == target.barcode:
        return 1.0
    # An explicit tester without an explicit tester classification on the
    # catalogue side is never enough for a high-confidence suggestion.
    if source.is_tester is True and target.is_tester is None:
        score = min(score, 0.79)
    if source.is_tester is None and target.is_tester is True:
        score = min(score, 0.79)
    # Missing physical attributes are uncertainty, not evidence of equality.
    if source.volume_ml is None or target.volume_ml is None:
        score = min(score, 0.87)
    if not source.concentration or not target.concentration:
        score = min(score, 0.94)
    return score


class SupplierAliasStore:
    """Approved aliases, stored locally and scoped to a specific supplier.

    A call to confirm is a human approval operation, never used in search.
    No mapping writes are made to Ginger.
    """

    SCHEMA_VERSION = 1

    def __init__(self, path: str | Path):
        self.path = Path(path)

    @staticmethod
    def _key(position: SupplierPosition) -> str:
        if not position.supplier_id.strip() or not position.title.strip():
            raise ValueError("Supplier ID and position title are required")
        return position.position_id.strip() or "name:" + normalize_text(position.title)

    def _read(self) -> dict[str, dict[str, dict[str, str]]]:
        if not self.path.exists():
            return {}
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValueError("Supplier mapping state is unreadable") from exc
        if not isinstance(raw, dict) or raw.get("schema_version") != self.SCHEMA_VERSION:
            raise ValueError("Unsupported supplier mapping schema")
        mappings = raw.get("mappings")
        if not isinstance(mappings, dict):
            raise ValueError("Invalid supplier mapping state")
        for supplier, entries in mappings.items():
            if not isinstance(supplier, str) or not isinstance(entries, dict):
                raise ValueError("Invalid supplier mapping entries")
            for key, item in entries.items():
                if not isinstance(key, str) or not isinstance(item, dict):
                    raise ValueError("Invalid supplier mapping entry")
                if not all(isinstance(item.get(f), str) and item[f]
                           for f in ("sku", "title")):
                    raise ValueError("Invalid supplier mapping entry")
        return mappings

    def get(self, position: SupplierPosition) -> tuple[str, str] | None:
        entry = self._read().get(position.supplier_id.strip(), {}).get(self._key(position))
        return None if entry is None else (entry["sku"], entry["title"])

    def confirm(self, position: SupplierPosition, sku: str, *, replace: bool = False) -> None:
        """Persist one explicitly approved binding; refuse silent reassignment."""
        sku = sku.strip()
        if not sku:
            raise ValueError("Catalogue SKU is required")
        key = self._key(position)
        mappings = self._read()
        entries = mappings.setdefault(position.supplier_id.strip(), {})
        previous = entries.get(key)
        if previous and previous["sku"] != sku and not replace:
            raise ValueError("Existing binding differs; explicit replace is required")
        entries[key] = {"sku": sku, "title": position.title}
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_name(self.path.name + ".tmp")
        temporary.write_text(json.dumps({
            "schema_version": self.SCHEMA_VERSION, "mappings": mappings,
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(self.path)


class GingerSupplierMatcher:
    """Rank candidates, but never approve or mutate the catalogue."""

    def __init__(self, catalogue: list[CatalogPosition], aliases: SupplierAliasStore | None = None):
        self.catalogue = {item.sku: item for item in catalogue}
        if not all(item.sku and item.title for item in catalogue) or len(self.catalogue) != len(catalogue):
            raise ValueError("Catalogue SKUs must be unique and nonempty")
        self.aliases = aliases

    def recommend(self, position: SupplierPosition, *, limit: int = 3) -> MatchResult:
        if not position.supplier_id.strip() or not position.title.strip():
            raise ValueError("Supplier ID and position title are required")
        if limit < 1:
            raise ValueError("Candidate limit must be positive")
        source = _features(position)
        ranked: list[Candidate] = []
        for item in self.catalogue.values():
            candidate_features = _features(item, brand_hint=position.brand)
            incoming_features = _features(position, brand_hint=item.brand)
            if not _compatible(incoming_features, candidate_features):
                continue
            score = _similarity(incoming_features, candidate_features)
            if score >= 0.45:
                ranked.append(Candidate(item.sku, item.title, round(score, 4)))
        ranked.sort(key=lambda c: (-c.score, c.sku))

        if self.aliases is not None:
            known = self.aliases.get(position)
            if known is not None:
                target = self.catalogue.get(known[0])
                if target is not None:
                    saved_title = SupplierPosition(position.supplier_id, known[1])
                    saved = _features(saved_title)
                    target_features = _features(target)
                    # A reused vendor ID, renamed product, or changed bottle size
                    # must not silently inherit an old supplier mapping.
                    if (sorted(source.name.split()) == sorted(saved.name.split())
                            and _compatible(source, saved)
                            and _compatible(_features(position, brand_hint=target.brand), target_features)):
                        return MatchResult("known", tuple(ranked[:limit]), target.sku)

        selected = tuple(ranked[:limit])
        if not selected:
            return MatchResult("unmatched", ())
        top = selected[0].score
        runner_up = ranked[1].score if len(ranked) > 1 else 0.0
        if top >= 0.90 and top - runner_up >= 0.10:
            return MatchResult("suggested", selected)
        return MatchResult("review", selected)
