"""Тесты просмотрщика: реальный рендер DXF (геометрия + подсветка) и деградация на схему."""
from __future__ import annotations

import pathlib
import sys

import ezdxf
import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.domain import ParsedDrawing  # noqa: E402
from app.parsing.dxf import parse_dxf  # noqa: E402
from app.rules.engine import run_checks  # noqa: E402
from app.svgview import render_drawing_svg  # noqa: E402


def test_render_real_dxf_has_geometry_and_red_highlight(tmp_path):
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (420, 0), (420, 297), (0, 297)])  # рамка A3
    msp.add_circle((200, 150), 50)  # немного реальной геометрии
    big = msp.add_text("надпись", dxfattribs={"height": 5}); big.dxf.insert = (30, 260)
    small = msp.add_text("мелочь", dxfattribs={"height": 2.0}); small.dxf.insert = (30, 250)  # 2.0 → major 2.304
    p = tmp_path / "r.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    findings = run_checks(parsed)
    svg = render_drawing_svg(str(p), parsed, findings)

    assert 'class="dxf-svg"' in svg                 # реальный рендер, не схема
    assert svg.count("<path") > 4, svg.count("<path")  # настоящая геометрия (линии/текст/окружность)
    assert "#ffffff" in svg                          # белый лист-«бумага»
    assert "#ff0000" in svg                          # красная подсветка major-замечания (2.304)


def test_render_pdf_raster_has_image_and_overlay(tmp_path):
    """Вектор-PDF рендерится РАСТРОМ страницы (data-URI) + рамки замечаний поверх."""
    pytest.importorskip("pypdfium2")
    fpdf_scripts = pytest.importorskip("scripts.make_bootstrap_pdf")
    from app.parsing.pdf import parse_pdf

    p = tmp_path / "d.pdf"
    fpdf_scripts.build_dirty(str(p))  # нестандартный формат + масштаб вне ряда + мелкий шрифт
    parsed = parse_pdf(str(p))
    findings = run_checks(parsed)
    svg = render_drawing_svg(str(p), parsed, findings)

    assert 'class="dxf-svg"' in svg                  # просмотрщик, не схема
    assert "data:image/png;base64," in svg           # страница как растровая картинка
    assert "<image" in svg
    assert "<rect" in svg                             # подсветка замечаний поверх растра
    assert "<title>" in svg                           # подсказка с пунктом ГОСТ по наведению


def test_render_falls_back_to_schematic_on_error():
    # битый путь + source_format=dxf → реальный рендер падает → деградация на схему (без исключения)
    parsed = ParsedDrawing(
        source_path="x", units_code=4, units_assumed=False, mm_per_unit=1.0,
        frame_size_mm=(420.0, 297.0), source_format="dxf",
    )
    svg = render_drawing_svg(r"Z:\nope\missing.dxf", parsed, [])
    assert "<svg" in svg  # схема-фоллбэк отдала валидный SVG, отчёт не потерян
