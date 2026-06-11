"""Генератор бутстрап-набора DXF (Этап -1 / Этап 0).

Реального корпуса чертежей пока нет (см. PLAN.md), поэтому для проверки ядра
генерируем синтетические DXF с ЗАЛОЖЕННЫМИ нарушениями:

- clean_a3.dxf  — формат A3, штамп заполнен, масштаб 1:1, высоты 3.5/5 мм, линия 0.5 мм → 0 замечаний;
- dirty_a3.dxf  — нестандартный формат 400×300 (2.301), пустая графа «обозначение»
                  (2.104), масштаб 1:3 вне ряда (2.302), текст высотой 2.0 мм (2.304),
                  толщина основной линии 2.0 мм вне 0,5–1,4 (2.303).

Это НЕ замена реальному корпусу — метрики на синтетике предварительны.
"""
from __future__ import annotations

import pathlib
import sys

import ezdxf

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "data" / "bootstrap"

TITLE_BLOCK = "ОСНОВНАЯ_НАДПИСЬ"


def _ensure_title_block_def(doc) -> None:
    """Определить блок штампа 185×55 с графами-атрибутами."""
    if TITLE_BLOCK in doc.blocks:
        return
    blk = doc.blocks.new(TITLE_BLOCK)
    blk.add_lwpolyline([(0, 0), (185, 0), (185, 55), (0, 55)])
    blk.add_attdef("НАИМЕНОВАНИЕ", insert=(95, 45), dxfattribs={"height": 5})
    blk.add_attdef("ОБОЗНАЧЕНИЕ", insert=(110, 12), dxfattribs={"height": 7})
    blk.add_attdef("МАСШТАБ", insert=(150, 45), dxfattribs={"height": 5})


def _add_text(msp, text: str, height: float, x: float, y: float) -> None:
    t = msp.add_text(text, dxfattribs={"height": height})
    t.dxf.insert = (x, y)


def _new_doc():
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4  # миллиметры
    return doc


def build_clean():
    doc = _new_doc()
    msp = doc.modelspace()
    w, h = 420, 297  # A3 (альбомная)
    msp.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h)], dxfattribs={"lineweight": 50})  # 0.5 мм — 2.303 ок
    _ensure_title_block_def(doc)
    ref = msp.add_blockref(TITLE_BLOCK, (w - 185, 0))
    ref.add_auto_attribs(
        {"НАИМЕНОВАНИЕ": "Вал", "ОБОЗНАЧЕНИЕ": "АБВГ.301261.001", "МАСШТАБ": "1:1"}
    )
    _add_text(msp, "Технические требования", 5.0, 30, 260)
    _add_text(msp, "1. *Размеры для справок.", 3.5, 30, 245)
    return doc


def build_dirty():
    doc = _new_doc()
    msp = doc.modelspace()
    w, h = 400, 300  # нестандартный формат -> ГОСТ 2.301
    msp.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h)], dxfattribs={"lineweight": 200})  # 2.0 мм -> 2.303
    _ensure_title_block_def(doc)
    ref = msp.add_blockref(TITLE_BLOCK, (w - 185, 0))
    ref.add_auto_attribs(
        {"НАИМЕНОВАНИЕ": "Кронштейн", "ОБОЗНАЧЕНИЕ": "", "МАСШТАБ": "1:3"}
    )  # пустое обозначение -> 2.104 ; масштаб 1:3 -> 2.302
    _add_text(msp, "мелкая надпись", 2.0, 30, 250)  # высота 2.0 мм -> 2.304
    _add_text(msp, "нормальная надпись", 5.0, 30, 235)
    return doc


def build_realistic_clean():
    """Чистый чертёж в стиле реального CAD-экспорта (КОМПАС): рамка и текст ВНУТРИ блоков,
    штамп БЕЗ ATTRIB (узнаётся по подписям ГОСТ).

    Калибровочный регресс по реальному DXF (2026-06-11): на таком чертеже не должно быть
    ложных 2.301 (формат) и 2.104 (нет штампа) — это ловушки, на которые раньше попадались.
    """
    doc = _new_doc()
    msp = doc.modelspace()
    w, h = 420, 297  # A3
    # Рамка — ВНУТРИ блока (не верхнеуровневая полилиния), как на реальных чертежах.
    if "FRAME_BLK" not in doc.blocks:
        fb = doc.blocks.new("FRAME_BLK")
        fb.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h), (0, 0)], dxfattribs={"lineweight": 60})
    msp.add_blockref("FRAME_BLK", (0, 0))
    # Штамп — блок с MTEXT-подписями ГОСТ в правом-нижнем углу, БЕЗ ATTRIB.
    # Имя блока намеренно ОПАЗНОЕ ("U7", как в реальном КОМПАС-экспорте) — чтобы штамп
    # распознавался по ПОДПИСЯМ граф, а не по имени блока.
    if "U7" not in doc.blocks:
        sb = doc.blocks.new("U7")
        cells = [
            ("Масштаб", 3.5, 150, 45), ("Масса", 3.5, 120, 45), ("Лит.", 3.5, 135, 45),
            ("Лист", 3.5, 150, 30), ("Листов", 3.5, 168, 30), ("Дата", 3.5, 170, 12),
            ("1:1", 5.0, 165, 40), ("Деталь", 10.0, 60, 30),
        ]
        for s, ht, x, y in cells:
            mt = sb.add_mtext(s)
            mt.dxf.char_height = ht
            mt.dxf.insert = (x, y)
    msp.add_blockref("U7", (w - 185, 0))
    doc.header["$EXTMIN"] = (0, 0, 0)
    doc.header["$EXTMAX"] = (w, h, 0)
    return doc


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    build_clean().saveas(OUT / "clean_a3.dxf")
    build_dirty().saveas(OUT / "dirty_a3.dxf")
    build_realistic_clean().saveas(OUT / "realistic_a3.dxf")
    print(f"Создан: {OUT / 'clean_a3.dxf'}")
    print(f"Создан: {OUT / 'dirty_a3.dxf'}")
    print(f"Создан: {OUT / 'realistic_a3.dxf'}")


if __name__ == "__main__":
    main()
