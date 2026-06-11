r"""Демо-набор для показа (PLAN.md, пункт E). Не замена реальному корпусу — это
выверенные файлы для НАГЛЯДНОЙ демонстрации движка инвесторам/предприятиям.

Два чертежа в стиле реального CAD-экспорта (геометрия детали + рамка + штамп), чтобы
подсветка замечаний ложилась на настоящий лист, а не на пустой прямоугольник:

  demo_clean.dxf  — корректный A3: формат, штамп заполнен, масштаб 1:1, высоты 3.5/5 мм,
                    основная линия 0.5 мм  → 0 замечаний (демонстрация «не врём на верном»).
  demo_dirty.dxf  — те же деталь и стиль, но с заложенными нарушениями всех правил P0:
                    формат 400×280 (2.301), пустая графа «обозначение» (2.104),
                    масштаб 1:3 вне ряда (2.302), текст 2.0 мм (2.304), линия 2.0 мм (2.303).

Запуск:  .\.venv\Scripts\python.exe scripts\make_demo_files.py
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
OUT = ROOT / "data" / "demo"
TITLE_BLOCK = "ОСНОВНАЯ_НАДПИСЬ"


def _title_block_def(doc) -> None:
    if TITLE_BLOCK in doc.blocks:
        return
    blk = doc.blocks.new(TITLE_BLOCK)
    blk.add_lwpolyline([(0, 0), (185, 0), (185, 55), (0, 55), (0, 0)], dxfattribs={"lineweight": 35})
    # внутренние линии граф — чтобы штамп выглядел как настоящий
    for x in (120, 150):
        blk.add_line((x, 0), (x, 55), dxfattribs={"lineweight": 18})
    for y in (18, 36):
        blk.add_line((120, y), (185, y), dxfattribs={"lineweight": 18})
    blk.add_attdef("НАИМЕНОВАНИЕ", insert=(10, 40), dxfattribs={"height": 5})
    blk.add_attdef("ОБОЗНАЧЕНИЕ", insert=(125, 42), dxfattribs={"height": 7})
    blk.add_attdef("МАСШТАБ", insert=(155, 42), dxfattribs={"height": 5})


def _new_doc():
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4  # мм
    return doc


def _draw_flange(msp, cx: float, cy: float, line_lw: int, text_h: float) -> None:
    """Похожая на реальную деталь: фланец — контур, отверстия, осевые, размерные подписи."""
    thin = {"lineweight": 18}      # тонкая линия (осевые/выносные)
    main = {"lineweight": line_lw}  # основная линия (контур)
    # контур фланца
    msp.add_circle((cx, cy), 70, dxfattribs=main)
    msp.add_circle((cx, cy), 28, dxfattribs=main)      # центральное отверстие
    # крепёжные отверстия по окружности
    import math
    for k in range(6):
        a = math.radians(60 * k)
        hx, hy = cx + 50 * math.cos(a), cy + 50 * math.sin(a)
        msp.add_circle((hx, hy), 6, dxfattribs=main)
    # осевые линии
    msp.add_line((cx - 85, cy), (cx + 85, cy), dxfattribs=thin)
    msp.add_line((cx, cy - 85), (cx, cy + 85), dxfattribs=thin)
    # размерные подписи (как на чертеже)
    t1 = msp.add_text("⌀140", dxfattribs={"height": text_h}); t1.dxf.insert = (cx - 12, cy + 78)
    t2 = msp.add_text("6 отв. ⌀12", dxfattribs={"height": text_h}); t2.dxf.insert = (cx + 40, cy + 55)


def _add_text(msp, s: str, h: float, x: float, y: float) -> None:
    t = msp.add_text(s, dxfattribs={"height": h})
    t.dxf.insert = (x, y)


def build_clean():
    doc = _new_doc()
    msp = doc.modelspace()
    w, h = 420, 297  # A3
    msp.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h), (0, 0)], dxfattribs={"lineweight": 50})  # 0.5 мм
    _title_block_def(doc)
    msp.add_blockref(TITLE_BLOCK, (w - 185, 0)).add_auto_attribs(
        {"НАИМЕНОВАНИЕ": "Фланец", "ОБОЗНАЧЕНИЕ": "АБВГ.301261.005", "МАСШТАБ": "1:1"})
    _draw_flange(msp, 180, 165, line_lw=50, text_h=5.0)
    _add_text(msp, "Технические требования", 5.0, 25, 70)
    _add_text(msp, "1. Острые кромки притупить.", 3.5, 25, 55)
    doc.header["$EXTMIN"] = (0, 0, 0); doc.header["$EXTMAX"] = (w, h, 0)
    return doc


def build_dirty():
    doc = _new_doc()
    msp = doc.modelspace()
    w, h = 400, 280  # нестандартный формат -> 2.301
    msp.add_lwpolyline([(0, 0), (w, 0), (w, h), (0, h), (0, 0)], dxfattribs={"lineweight": 200})  # 2.0 мм -> 2.303
    _title_block_def(doc)
    msp.add_blockref(TITLE_BLOCK, (w - 185, 0)).add_auto_attribs(
        {"НАИМЕНОВАНИЕ": "Кронштейн", "ОБОЗНАЧЕНИЕ": "", "МАСШТАБ": "1:3"})  # пусто -> 2.104; 1:3 -> 2.302
    _draw_flange(msp, 175, 160, line_lw=200, text_h=2.0)  # текст 2.0 мм -> 2.304
    _add_text(msp, "примечание мелким шрифтом", 2.0, 25, 60)  # ещё одна 2.0 мм
    doc.header["$EXTMIN"] = (0, 0, 0); doc.header["$EXTMAX"] = (w, h, 0)
    return doc


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    build_clean().saveas(OUT / "demo_clean.dxf")
    build_dirty().saveas(OUT / "demo_dirty.dxf")
    print(f"Создан: {OUT / 'demo_clean.dxf'}")
    print(f"Создан: {OUT / 'demo_dirty.dxf'}")


if __name__ == "__main__":
    main()
