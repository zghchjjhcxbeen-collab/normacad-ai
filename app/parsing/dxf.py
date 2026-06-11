"""Парсер DXF на базе ezdxf.

Извлекает только то, что нужно проверкам P0:
- единицы чертежа ($INSUNITS) → множитель в мм;
- габарит рамки листа (для ГОСТ 2.301);
- текстовые примитивы (TEXT/MTEXT) с высотой в мм — для ГОСТ 2.304;
- основную надпись (штамп) и её графы — для ГОСТ 2.104/2.302.

Калибровка на реальном DXF (КОМПАС-экспорт, 2026-06-11) выявила и закрыла три ловушки,
которых не было на синтетике (HANDOFF, грабли №9):
1) ТЕКСТ И РАМКА ЖИВУТ ВНУТРИ БЛОКОВ. На реальных чертежах надписи и рамка вставлены
   блоками (INSERT), а не лежат в модельном пространстве. Поэтому текст собираем
   РЕКУРСИВНО (взрываем блоки через virtual_entities).
2) РАМКА — НЕ ПОЛИЛИНИЯ. Габарит листа берём из нескольких кандидатов (полилиния,
   $EXTMIN/$EXTMAX, точный bbox) и «прилипаем» к стандартному формату, если совпал —
   иначе общий bbox даёт ЛОЖНОЕ 2.301 на корректном листе.
3) ШТАМП БЕЗ ATTRIB. Реальный штамп — это линии и тексты, без ATTRIB-тегов. Распознаём
   его по СТАНДАРТНЫМ ПОДПИСЯМ ГОСТ (Масштаб/Лист/Масса…) в правом-нижнем углу; значения
   граф при этом надёжно не считать → заполненность не проверяем (только факт наличия).

Парсер НИЧЕГО не проверяет — он только достаёт факты. Проверки — в app/rules.
"""
from __future__ import annotations

import re

import ezdxf
from ezdxf import bbox

from app.domain import ParsedDrawing, TextItem, TitleBlock
from app.ruleset import (
    FORMAT_TOLERANCE_MM,
    FORMATS_MM,
    TITLE_BLOCK_NAME_PATTERNS,
    TITLE_BLOCK_SIZE_MM,
)

# $INSUNITS → (множитель в мм, нужно_ли_предполагать)
# Коды по спецификации DXF.
_UNIT_TO_MM: dict[int, float] = {
    0: 1.0,      # unitless — предполагаем мм
    1: 25.4,     # дюймы
    2: 304.8,    # футы
    4: 1.0,      # миллиметры
    5: 10.0,     # сантиметры
    6: 1000.0,   # метры
    8: 0.001,    # микроны
    9: 1.0,      # дециметры? (редко) — трактуем осторожно ниже
}

_TITLE_NAME_RE = re.compile("|".join(TITLE_BLOCK_NAME_PATTERNS), re.IGNORECASE)

# Стандартные подписи граф основной надписи ГОСТ 2.104 — по ним опознаём штамп без ATTRIB.
_STAMP_LABELS = (
    "МАСШТАБ", "ЛИСТОВ", "ЛИСТ", "МАССА", "ЛИТ", "ИЗМ", "ПОДП", "ДАТА",
    "РАЗРАБ", "ПРОВ", "Н.КОНТР", "НКОНТР", "УТВ", "ФОРМАТ", "ДОКУМ", "ИНВ",
)
# Токен масштаба в тексте штампа: «1:2», «2,5:1» и т.п.
_SCALE_TOKEN_RE = re.compile(r"\d+(?:[.,]\d+)?\s*:\s*\d+(?:[.,]\d+)?")


def _has_title_graphs(attribs: dict[str, str]) -> bool:
    """Похож ли набор ATTRIB на штамп: есть ≥2 граф из наимен./обозн./масштаб."""
    tags = " ".join(attribs)
    return sum(s in tags for s in ("НАИМЕН", "ОБОЗН", "МАСШТАБ", "SCALE")) >= 2


def _iter_all(entities, depth: int = 0):
    """Рекурсивный обход: вставки (INSERT) взрываем, чтобы видеть текст ВНУТРИ блоков.

    На реальных чертежах надписи штампа и рамка вставлены блоками. virtual_entities()
    отдаёт содержимое блока, приведённое к мировым координатам; ATTDEF-шаблоны не
    отдаются (значения ATTRIB читаются отдельно в логике штампа). Глубина ограничена.
    """
    for e in entities:
        if e.dxftype() == "INSERT" and depth < 6:
            try:
                yield from _iter_all(e.virtual_entities(), depth + 1)
            except Exception:  # noqa: BLE001 — экзотический блок просто пропускаем
                continue
        else:
            yield e


def _looks_like_scale(token: str) -> bool:
    """Похоже ли на масштаб ЕСКД: одна сторона = 1, обе ≤ 1000 (отсекает даты/массы)."""
    m = re.search(r"(\d+(?:[.,]\d+)?)\s*:\s*(\d+(?:[.,]\d+)?)", token)
    if not m:
        return False
    try:
        a = float(m.group(1).replace(",", ".")); b = float(m.group(2).replace(",", "."))
    except ValueError:
        return False
    if a <= 0 or b <= 0 or max(a, b) > 1000:
        return False
    return a == 1 or b == 1


def _detect_stamp_by_labels(texts: list[TextItem], frame_size_mm: tuple[float, float] | None) -> TitleBlock | None:
    """Распознать штамп по подписям граф ГОСТ в правом-нижнем углу (когда нет ATTRIB).

    Нужно ≥3 стандартных подписи (Масштаб/Лист/Масса…) в зоне штампа. Значения граф
    при этом не размечены → values_reliable=False (заполненность не проверяем). Масштаб,
    если в зоне есть валидный токен «1:n», кладём в графу — его 2.302 проверить можно.
    """
    if not frame_size_mm:
        return None
    w, h = frame_size_mm
    zone = [t for t in texts if t.insert_mm[0] > 0.5 * w and t.insert_mm[1] < 0.35 * h]
    if not zone:
        return None
    found = {lbl for t in zone for lbl in _STAMP_LABELS if lbl in t.text.upper()}
    if len(found) < 3:
        return None
    joined = " ".join(t.text for t in zone)
    scale = next(
        (m.group(0) for m in _SCALE_TOKEN_RE.finditer(joined) if _looks_like_scale(m.group(0))),
        None,
    )
    bw, bh = TITLE_BLOCK_SIZE_MM
    return TitleBlock(
        present=True,
        attribs={"МАСШТАБ": scale} if scale else {},
        bbox_mm=(round(max(w - bw, 0.0), 2), 0.0, bw, bh),
        block_handle=None,
        values_reliable=False,  # значения граф из геометрии надёжно не читаются
    )


def _matches_standard(size: tuple[float, float]) -> bool:
    """Совпадает ли габарит со стандартным форматом A4…A0 в пределах допуска."""
    dims = sorted(size)
    for short, long in FORMATS_MM.values():
        std = sorted((short, long))
        if abs(dims[0] - std[0]) <= FORMAT_TOLERANCE_MM and abs(dims[1] - std[1]) <= FORMAT_TOLERANCE_MM:
            return True
    return False


def _frame_candidates(doc, msp, mm_per_unit: float) -> list[tuple[str, tuple[float, float]]]:
    """Кандидаты габарита листа: полилиния-рамка, $EXTMIN/$EXTMAX, точный bbox примитивов."""
    cands: list[tuple[str, tuple[float, float]]] = []
    poly = _largest_rectangle_mm(msp, mm_per_unit)
    if poly:
        cands.append(("полилиния-рамка", poly))
    try:
        emin = doc.header.get("$EXTMIN"); emax = doc.header.get("$EXTMAX")
        if emin and emax:
            w = (float(emax[0]) - float(emin[0])) * mm_per_unit
            h = (float(emax[1]) - float(emin[1])) * mm_per_unit
            if w > 1 and h > 1:
                cands.append(("границы $EXTMIN/$EXTMAX", (round(w, 2), round(h, 2))))
    except Exception:  # noqa: BLE001
        pass
    try:
        ext = bbox.extents(msp, fast=False)
        if ext.has_data:
            cands.append(("габарит примитивов", (round(ext.size.x * mm_per_unit, 2),
                                                 round(ext.size.y * mm_per_unit, 2))))
    except Exception:  # noqa: BLE001
        pass
    return cands


def _collect_lineweights_mm(msp, doc) -> list[float]:
    """Явные положительные толщины линий (мм) для ГОСТ 2.303. BYLAYER → из слоя.

    ezdxf хранит lineweight в сотых мм (50 = 0,5 мм); спец-значения -1 BYLAYER,
    -2 BYBLOCK, -3 DEFAULT и 0 — пропускаем. Если толщина не задана явно (часто
    задаётся стилем печати) — её тут не будет, и правило 2.303 просто не запустится.
    """
    layer_lw: dict[str, int] = {}
    try:
        for layer in doc.layers:
            lw = int(getattr(layer.dxf, "lineweight", -3) or -3)
            if lw > 0:
                layer_lw[str(layer.dxf.name)] = lw
    except Exception:  # noqa: BLE001 — экзотические таблицы слоёв пропускаем
        pass
    weights: set[int] = set()
    for e in msp:
        try:
            lw = int(getattr(e.dxf, "lineweight", -1) or -1)
        except (TypeError, ValueError):
            continue
        if lw > 0:
            weights.add(lw)
        elif lw == -1:  # BYLAYER
            ll = layer_lw.get(str(getattr(e.dxf, "layer", "0")))
            if ll:
                weights.add(ll)
    return sorted(w / 100.0 for w in weights)


def _mm_factor(units_code: int) -> tuple[float, bool]:
    """Вернуть (множитель в мм, units_assumed)."""
    if units_code == 0:
        return 1.0, True
    return _UNIT_TO_MM.get(units_code, 1.0), units_code not in _UNIT_TO_MM


def _largest_rectangle_mm(msp, mm_per_unit: float) -> tuple[float, float] | None:
    """Габарит внешней рамки = наибольший по площади замкнутый контур (LWPOLYLINE).

    На реальных чертежах рамка ГОСТ — отдельная полилиния у краёв листа. Берём ЕЁ,
    а не общий bbox всех примитивов: иначе текст/выноска за рамкой «раздувают» габарит
    и дают ЛОЖНОЕ замечание 2.301 (см. HANDOFF, грабли №6).
    """
    best_area = 0.0
    best_wh: tuple[float, float] | None = None
    for e in msp.query("LWPOLYLINE"):
        try:
            pts = [(float(p[0]), float(p[1])) for p in e.get_points()]
        except Exception:  # noqa: BLE001 — экзотические полилинии пропускаем
            continue
        if len(pts) < 4:
            continue
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        w, h = max(xs) - min(xs), max(ys) - min(ys)
        area = w * h
        if w > 0 and h > 0 and area > best_area:
            best_area, best_wh = area, (w, h)
    if best_wh is None:
        return None
    return (round(best_wh[0] * mm_per_unit, 2), round(best_wh[1] * mm_per_unit, 2))


def parse_dxf(path: str) -> ParsedDrawing:
    """Распарсить DXF-файл в ParsedDrawing.

    Бросает ezdxf.DXFStructureError / IOError при битом или недоступном файле —
    воркер обязан ловить это и переводить задачу в статус failed.
    """
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()

    units_code = int(doc.header.get("$INSUNITS", 0) or 0)
    mm_per_unit, units_assumed = _mm_factor(units_code)

    warnings: list[str] = []
    if units_assumed:
        warnings.append(
            "Единицы чертежа не заданы ($INSUNITS=0) — приняты миллиметры. "
            "Геометрические проверки в мм могут быть недостоверны."
        )

    # --- Габарит листа: из кандидатов выбираем совпавший со стандартным форматом ---
    # На реальных чертежах рамка не полилиния и лежит в блоках, а общий bbox «раздувается»
    # текстом/выносками → ложное 2.301. Поэтому собираем несколько кандидатов и «прилипаем»
    # к стандарту: если какой-то габарит — это формат ряда, он и есть рамка листа.
    frame_size_mm: tuple[float, float] | None = None
    cands = _frame_candidates(doc, msp, mm_per_unit)
    for _label, size in cands:
        if _matches_standard(size):
            frame_size_mm = size
            break
    if frame_size_mm is None and cands:
        label, size = max(cands, key=lambda c: c[1][0] * c[1][1])  # наибольший по площади
        frame_size_mm = size
        warnings.append(
            f"Рамка не распознана как стандартный формат — габарит взят по «{label}» "
            f"({size[0]:g}×{size[1]:g} мм), формат может быть неточным."
        )

    # --- Тексты (TEXT + MTEXT), РЕКУРСИВНО (в т.ч. внутри блоков штампа/рамки) ---
    texts: list[TextItem] = []
    for e in _iter_all(msp):
        t = e.dxftype()
        try:
            if t == "TEXT":
                s = str(e.dxf.text)
                height = float(e.dxf.height)
            elif t == "MTEXT":
                s = e.plain_text()           # снять инлайн-коды \H \W \Q \c …
                height = float(e.dxf.char_height)
            else:
                continue
            ins = e.dxf.insert
        except Exception:  # noqa: BLE001 — повреждённый примитив пропускаем
            continue
        if not s.strip():
            continue
        texts.append(
            TextItem(
                text=s,
                height_mm=round(height * mm_per_unit, 3),
                insert_mm=(round(ins.x * mm_per_unit, 2), round(ins.y * mm_per_unit, 2)),
                handle=str(getattr(e.dxf, "handle", "") or ""),  # у виртуальных может не быть
                style=str(getattr(e.dxf, "style", "")),
            )
        )

    # --- Основная надпись: INSERT по ИМЕНИ (похоже на штамп) ИЛИ по СОДЕРЖИМОМУ ---
    # На реальных чертежах блок штампа может называться как угодно, поэтому если имя
    # не распознано — принимаем блок, чьи ATTRIB содержат графы штампа (наимен./обозн./
    # масштаб). Это снижает ложное «штамп не обнаружен» (critical) на чужих чертежах.
    title_block: TitleBlock | None = None
    fallback: TitleBlock | None = None
    for ins in msp.query("INSERT"):
        name = str(ins.dxf.name)
        attribs = {str(a.dxf.tag).upper(): str(a.dxf.text) for a in ins.attribs}
        p = ins.dxf.insert
        w, h = TITLE_BLOCK_SIZE_MM
        tb = TitleBlock(
            present=True,
            attribs=attribs,
            bbox_mm=(round(p.x * mm_per_unit, 2), round(p.y * mm_per_unit, 2), w, h),
            block_handle=ins.dxf.handle,
            # графам можно доверять только если ATTRIB реально есть; блок по имени без
            # ATTRIB (геометрический штамп) → значения не считать (иначе ложные «графа пуста»)
            values_reliable=bool(attribs),
        )
        if _TITLE_NAME_RE.search(name):
            title_block = tb
            break
        if fallback is None and _has_title_graphs(attribs):
            fallback = tb

    # Порядок: штамп по имени блока → по графам ATTRIB → по подписям ГОСТ в углу → нет штампа.
    title_block = (
        title_block
        or fallback
        or _detect_stamp_by_labels(texts, frame_size_mm)
        or TitleBlock(present=False)
    )

    return ParsedDrawing(
        source_path=path,
        units_code=units_code,
        units_assumed=units_assumed,
        mm_per_unit=mm_per_unit,
        frame_size_mm=frame_size_mm,
        texts=texts,
        title_block=title_block,
        warnings=warnings,
        line_weights_mm=_collect_lineweights_mm(msp, doc),
    )
