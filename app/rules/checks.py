"""Четыре детерминированные проверки P0 (см. PLAN.md, раздел 5).

Каждая проверка — чистая функция (ParsedDrawing) -> list[Finding].
Числа берутся ТОЛЬКО из ruleset.py (версионируемый конфиг).
Каждое замечание несёт: локатор (handle/bbox) и рекомендацию «как исправить».
"""
from __future__ import annotations

import re

from app.domain import Finding, ParsedDrawing, Severity, Source
from app.ruleset import (
    ALLOWED_SCALES,
    FONT_ABS_MIN_MM,
    FONT_HEIGHT_TOLERANCE_MM,
    FONT_HEIGHTS_MM,
    FORMAT_TOLERANCE_MM,
    FORMATS_MM,
    LINE_MAIN_MAX_MM,
    LINE_MAIN_MIN_MM,
    RULE_META,
    TITLE_BLOCK_REQUIRED_GRAPHS,
)

_SCALE_RE = re.compile(r"(\d+(?:[.,]\d+)?)\s*:\s*(\d+(?:[.,]\d+)?)")


# ---------- помощники ----------

def _meta(key: str) -> dict:
    return RULE_META[key]


def normalize_scale(raw: str | None) -> str | None:
    """'М 1:2,5' / '1 : 2.5' -> '1:2.5'. Вернёт None, если масштаб не распознан."""
    if not raw:
        return None
    m = _SCALE_RE.search(raw)
    if not m:
        return None

    def clean(x: str) -> str:
        x = x.replace(",", ".")
        return x[:-2] if x.endswith(".0") else x

    return f"{clean(m.group(1))}:{clean(m.group(2))}"


def match_format(width_mm: float, height_mm: float, tol: float) -> str | None:
    dims = sorted((width_mm, height_mm))
    for name, (short, long) in FORMATS_MM.items():
        std = sorted((short, long))
        if abs(dims[0] - std[0]) <= tol and abs(dims[1] - std[1]) <= tol:
            return name
    return None


def nearest_format(width_mm: float, height_mm: float) -> tuple[str, float, float] | None:
    dims = sorted((width_mm, height_mm))
    best = None
    best_d = None
    for name, (short, long) in FORMATS_MM.items():
        std = sorted((short, long))
        d = abs(dims[0] - std[0]) + abs(dims[1] - std[1])
        if best_d is None or d < best_d:
            best_d, best = d, (name, short, long)
    return best


def _ratio(scale: str) -> float | None:
    try:
        a, b = scale.split(":")
        return float(a) / float(b)
    except (ValueError, ZeroDivisionError):
        return None


def nearest_scale(norm: str) -> str | None:
    target = _ratio(norm)
    if target is None:
        return None
    best = None
    best_d = None
    for s in ALLOWED_SCALES:
        v = _ratio(s)
        if v is None:
            continue
        d = abs(v - target)
        if best_d is None or d < best_d:
            best_d, best = d, s
    return best


def nearest_height(hh: float) -> float:
    return min(FONT_HEIGHTS_MM, key=lambda v: abs(v - hh))


# ---------- проверки ----------

def check_format_2301(d: ParsedDrawing) -> list[Finding]:
    m = _meta("2.301")
    if d.frame_size_mm is None:
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.INFO,
            "Не удалось определить габарит рамки — формат не проверен.",
            fix="Убедитесь, что на листе есть внешняя рамка по ГОСТ 2.301.",
            source=Source.DETERMINISTIC,
        )]
    w, h = d.frame_size_mm
    if match_format(w, h, FORMAT_TOLERANCE_MM) is None:
        nf = nearest_format(w, h)
        fix = "Приведите рамку к стандартному формату ряда A4…A0."
        if nf:
            fix += f" Ближайший — {nf[0]} ({nf[1]:g}×{nf[2]:g} мм)."
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.MAJOR,
            f"Габарит рамки {w:g}×{h:g} мм не соответствует ряду основных форматов A4…A0.",
            fix=fix, source=Source.DETERMINISTIC, bbox=(0.0, 0.0, w, h),
        )]
    return []


def check_scale_2302(d: ParsedDrawing) -> list[Finding]:
    m = _meta("2.302")
    tb = d.title_block
    if tb is None or not tb.present:
        return []
    raw = tb.graph("МАСШТАБ", "SCALE")
    if not raw or not raw.strip():
        return []  # пустую графу ловит 2.104
    norm = normalize_scale(raw)
    handle, bbox = tb.block_handle, tb.bbox_mm
    if norm is None:
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.MAJOR,
            f"Не удалось распознать масштаб в графе 6: «{raw}».",
            fix="Укажите масштаб в виде из ряда ГОСТ 2.302, например 1:1, 1:2 или 2:1.",
            source=Source.DETERMINISTIC, handle=handle, bbox=bbox,
        )]
    if norm not in ALLOWED_SCALES:
        ns = nearest_scale(norm)
        fix = "Замените масштаб на значение из ряда ГОСТ 2.302."
        if ns:
            fix += f" Ближайшее допустимое — {ns}."
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.MAJOR,
            f"Масштаб {norm} не входит в ряд по ГОСТ 2.302.",
            fix=fix, source=Source.DETERMINISTIC, handle=handle, bbox=bbox,
        )]
    return []


def check_fonts_2304(d: ParsedDrawing) -> list[Finding]:
    m = _meta("2.304")
    findings: list[Finding] = []
    seen: dict[float, str] = {}
    for t in d.texts:
        hh = round(t.height_mm, 2)
        if hh <= 0:
            continue
        in_series = any(abs(hh - s) <= FONT_HEIGHT_TOLERANCE_MM for s in FONT_HEIGHTS_MM)
        if in_series and hh >= FONT_ABS_MIN_MM:
            continue
        if hh in seen:
            continue
        seen[hh] = t.handle
        tx, ty = t.insert_mm
        tw = max(len(t.text) * hh * 0.6, hh)
        loc = (tx, ty, round(tw, 1), hh)
        if hh < FONT_ABS_MIN_MM:
            findings.append(Finding(
                m["code"], m["clause"], m["title"], Severity.MAJOR,
                f"Высота текста {hh:g} мм меньше минимально допустимой {FONT_ABS_MIN_MM:g} мм.",
                fix="Увеличьте высоту шрифта до значения из ряда — не менее 3,5 мм "
                    "для основных надписей (абсолютный минимум 2,5 мм).",
                source=Source.DETERMINISTIC, handle=t.handle, bbox=loc,
            ))
        else:
            findings.append(Finding(
                m["code"], m["clause"], m["title"], Severity.MINOR,
                f"Высота текста {hh:g} мм не входит в ряд "
                f"{', '.join(f'{v:g}' for v in FONT_HEIGHTS_MM)} мм.",
                fix=f"Приведите высоту к ближайшему значению ряда — {nearest_height(hh):g} мм.",
                source=Source.DETERMINISTIC, handle=t.handle, bbox=loc,
            ))
    return findings


def check_title_block_2104(d: ParsedDrawing) -> list[Finding]:
    m = _meta("2.104")
    tb = d.title_block
    if tb is None or not tb.present:
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.CRITICAL,
            "Основная надпись (штамп) не обнаружена.",
            fix="Добавьте основную надпись по ГОСТ 2.104 (форма 1, 185×55 мм) "
                "в правый нижний угол листа.",
            source=Source.DETERMINISTIC,
        )]
    # Штамп распознан по геометрии/подписям (нет ATTRIB-тегов) — значения граф надёжно
    # не считать. Подтверждаем наличие, заполненность — на ручную проверку (precision-first).
    if not getattr(tb, "values_reliable", True):
        return [Finding(
            m["code"], m["clause"], m["title"], Severity.INFO,
            "Основная надпись обнаружена; заполненность обязательных граф "
            "автоматически не проверена (графы не размечены тегами).",
            fix="Проверьте заполнение граф основной надписи вручную "
                "(наименование, обозначение, масштаб, подписи, даты).",
            source=Source.DETERMINISTIC, bbox=tb.bbox_mm,
        )]
    findings: list[Finding] = []
    for human_name, tag_subs in TITLE_BLOCK_REQUIRED_GRAPHS.items():
        value = tb.graph(*tag_subs)
        if value is None or not value.strip():
            findings.append(Finding(
                m["code"], m["clause"], m["title"], Severity.MAJOR,
                f"Не заполнена обязательная графа основной надписи: {human_name}.",
                fix=f"Заполните графу основной надписи: {human_name}.",
                source=Source.DETERMINISTIC, handle=tb.block_handle, bbox=tb.bbox_mm,
            ))
    return findings


def check_lines_2303(d: ParsedDrawing) -> list[Finding]:
    """Толщина основной (сплошной толстой) линии s ∈ [0,5; 1,4] мм по ГОСТ 2.303.

    Проверяем ТОЛЬКО при наличии явных толщин (lineweight). Если толщина задаётся
    стилем печати (как часто бывает) — линий нет в данных, и правило молчит,
    а не выдаёт ложное замечание. Тонкие линии/равномерность — следующая итерация.
    """
    m = _meta("2.303")
    weights = sorted({w for w in d.line_weights_mm if w and w > 0})
    if not weights:
        return []
    s_main = max(weights)
    if LINE_MAIN_MIN_MM - 0.01 <= s_main <= LINE_MAIN_MAX_MM + 0.01:
        return []
    return [Finding(
        m["code"], m["clause"], m["title"], Severity.MINOR,
        f"Толщина основной линии {s_main:g} мм вне диапазона ГОСТ 2.303 (0,5–1,4 мм).",
        fix="Задайте толщину основной (сплошной толстой) линии в пределах 0,5–1,4 мм; "
            "тонкие линии — от s/3 до s/2.",
        source=Source.DETERMINISTIC,
    )]
