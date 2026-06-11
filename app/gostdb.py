"""Доступ к базе ГОСТов (справочник): поиск по index.json и чтение выдержек.

Используется ИИ-чатом (RAG) и в будущем vision-слоем. Источник истины — text/.
Тексты ГОСТ наружу целиком не отдаём (копирайт) — только короткие выдержки.
"""
from __future__ import annotations

import functools
import json
import re

from app.config import settings

_WORD_RE = re.compile(r"[а-яёa-z0-9.]+", re.IGNORECASE)


@functools.lru_cache(maxsize=1)
def load_index() -> dict:
    p = settings.gost_db_dir / "index.json"
    if not p.exists():
        return {"gosts": []}
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"gosts": []}


def all_gosts() -> list[dict]:
    return load_index().get("gosts", [])


def by_number(number: str) -> dict | None:
    key = number.replace(" ", "").lower()
    for g in all_gosts():
        if g.get("number", "").replace(" ", "").lower() == key:
            return g
    return None


def _tokens(text: str) -> list[str]:
    return [t for t in _WORD_RE.findall(text.lower()) if len(t) >= 3]


def search(query: str, limit: int = 3) -> list[dict]:
    """Простой лексический поиск по номеру/названию/ключевым словам/темам."""
    q = _tokens(query)
    if not q:
        return []
    scored: list[tuple[int, dict]] = []
    for g in all_gosts():
        if not g.get("has_text"):
            continue
        number = g.get("number", "").lower().replace(" ", "")
        title = g.get("title", "").lower()
        kws = [k.lower() for k in g.get("keywords", [])]
        topics = [t.lower() for t in g.get("topics", [])]
        score = 0
        for tok in q:
            if tok in number:
                score += 8
            if tok in title:
                score += 3
            for kw in kws:
                if tok in kw or kw in tok:
                    score += 2
            for tp in topics:
                if tok in tp:
                    score += 1
        if score:
            scored.append((score, g))
    scored.sort(key=lambda x: (-x[0], x[1].get("number", "")))
    return [g for _, g in scored[:limit]]


def _read_full_text(g: dict) -> str:
    rel = (g.get("files") or {}).get("text")
    if not rel:
        return ""
    p = settings.gost_db_dir / rel
    if not p.exists():
        return ""
    try:
        return p.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def read_excerpt(g: dict, max_chars: int = 1000) -> str:
    txt = _read_full_text(g)
    if not txt:
        return ""
    txt = re.sub(r"\n{3,}", "\n\n", txt).strip()
    return txt[:max_chars]


_PAGE_RE = re.compile(r"===\s*Страница\s*\d+\s*===")


def _snippet_around(full: str, idx: int, max_chars: int) -> str:
    """Вырезать читаемый фрагмент, начиная со строки с якорем (часто это номер пункта)."""
    line_start = full.rfind("\n", 0, idx)
    start = line_start + 1 if line_start != -1 else idx
    chunk = full[start:start + max_chars + 240]
    chunk = re.sub(r"[\xad\-]\s*\n\s*", "", chunk)   # склейка переносов слов (OCR, мягкий перенос U+00AD)
    chunk = _PAGE_RE.sub(" ", chunk)
    chunk = re.sub(r"\s+", " ", chunk).strip()
    if " " in chunk:                                   # отрезать короткий обрывок слова в начале
        first = chunk.split(" ", 1)[0]
        if first.islower() and first.isalpha() and len(first) <= 4:
            chunk = chunk.split(" ", 1)[1]
    if len(chunk) > max_chars:
        cut = chunk.rfind(". ", 0, max_chars)
        chunk = chunk[: cut + 1] if cut > 60 else chunk[:max_chars].rstrip() + "…"
    return chunk


@functools.lru_cache(maxsize=64)
def grounding_quote(
    number: str, anchors: tuple[str, ...], max_chars: int = 300
) -> dict | None:
    """Найти в тексте ГОСТа из базы реальную формулировку пункта по якорной фразе.

    Анти-галлюцинация: возвращаем ТОЛЬКО текст, который буквально есть в базе.
    Если ни один якорь не найден — None (тогда показываем просто ссылку на стандарт).
    Числа отсюда НЕ берём — только прозаические формулировки (числа из ruleset.py).
    """
    g = by_number(number)
    if not g:
        return None
    full = _read_full_text(g)
    if not full:
        return None
    low = full.lower()
    for a in anchors:
        i = low.find(a.lower())
        if i != -1:
            quote = _snippet_around(full, i, max_chars)
            if quote:
                return {
                    "number": g.get("number", number),
                    "title": g.get("title", ""),
                    "quote": quote,
                    "anchor": a,
                }
    return None
