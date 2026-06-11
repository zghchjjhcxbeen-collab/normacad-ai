"""Тесты парсера DXF — устойчивость детекта рамки/формата к шуму (качество ядра).

Адверсариально: текст за рамкой НЕ должен раздувать габарит и давать ложное 2.301
(прямой ответ на грабли №6 из HANDOFF).
"""
from __future__ import annotations

import pathlib
import sys

import ezdxf

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.parsing.dxf import parse_dxf  # noqa: E402
from app.rules.engine import run_checks  # noqa: E402


def test_frame_not_inflated_by_outside_text(tmp_path):
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (420, 0), (420, 297), (0, 297)])  # рамка A3
    t = msp.add_text("метка за рамкой", dxfattribs={"height": 5})
    t.dxf.insert = (600, 400)  # текст ДАЛЕКО за рамкой — раздул бы общий bbox
    p = tmp_path / "a3_outside_text.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    assert parsed.frame_size_mm is not None
    w, h = parsed.frame_size_mm
    assert abs(w - 420) <= 1 and abs(h - 297) <= 1, parsed.frame_size_mm
    # ключевое обещание качества: НЕТ ложного 2.301 из-за текста за рамкой
    codes = {f.rule_code for f in run_checks(parsed)}
    assert "ГОСТ 2.301-68" not in codes, "ложное 2.301 от текста за рамкой"


def test_title_block_detected_by_content_not_only_name(tmp_path):
    from app.domain import Severity

    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    blk = doc.blocks.new("FRAME_42")  # имя НЕ похоже на штамп
    blk.add_lwpolyline([(0, 0), (185, 0), (185, 55), (0, 55)])
    blk.add_attdef("НАИМЕНОВАНИЕ", insert=(95, 45), dxfattribs={"height": 5})
    blk.add_attdef("ОБОЗНАЧЕНИЕ", insert=(110, 12), dxfattribs={"height": 7})
    blk.add_attdef("МАСШТАБ", insert=(150, 45), dxfattribs={"height": 5})
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (420, 0), (420, 297), (0, 297)])
    ref = msp.add_blockref("FRAME_42", (235, 0))
    ref.add_auto_attribs({"НАИМЕНОВАНИЕ": "Вал", "ОБОЗНАЧЕНИЕ": "АБВГ.301261.001", "МАСШТАБ": "1:1"})
    p = tmp_path / "named_frame.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    assert parsed.title_block is not None and parsed.title_block.present, "штамп не распознан по содержимому"
    # и нет ложного critical «штамп не обнаружен»
    crit = [
        f for f in run_checks(parsed)
        if f.rule_code == "ГОСТ 2.104-2006" and f.severity == Severity.CRITICAL
    ]
    assert not crit, "ложный critical «штамп не обнаружен» при нестандартном имени блока"


def test_lineweight_collected_and_flags_2303(tmp_path):
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (420, 0), (420, 297), (0, 297)], dxfattribs={"lineweight": 200})
    p = tmp_path / "thick.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    assert any(abs(w - 2.0) < 0.01 for w in parsed.line_weights_mm), parsed.line_weights_mm
    assert any(f.rule_code == "ГОСТ 2.303-68" for f in run_checks(parsed)), "2.303 не сработал на 2.0 мм"


def test_fallback_warns_without_frame_polyline(tmp_path):
    doc = ezdxf.new("R2010", setup=True)
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    t = msp.add_text("одинокий текст", dxfattribs={"height": 5})
    t.dxf.insert = (10, 10)
    p = tmp_path / "no_frame.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    # без рамки-контура парсер не падает и честно предупреждает о неточности формата
    assert any("рамка не распознана" in w.lower() for w in parsed.warnings), parsed.warnings


# --- Калибровка на реальном DXF (КОМПАС-экспорт, 2026-06-11): текст и рамка в блоках,
#     штамп без ATTRIB. Регресс: на корректном чертеже не должно быть ложных замечаний. ---

def test_text_inside_block_feeds_font_check(tmp_path):
    """Текст ВНУТРИ блока должен извлекаться рекурсивно и доходить до проверки 2.304."""
    doc = ezdxf.new("R2018", setup=True)
    doc.header["$INSUNITS"] = 4
    blk = doc.blocks.new("NOTE_BLK")
    mt = blk.add_mtext("мелкий текст")
    mt.dxf.char_height = 2.0  # < 2,5 мм → должно сработать 2.304
    mt.dxf.insert = (10, 10)
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (420, 0), (420, 297), (0, 297)])  # рамка A3 → без ложного 2.301
    msp.add_blockref("NOTE_BLK", (50, 50))
    p = tmp_path / "text_in_block.dxf"
    doc.saveas(str(p))

    parsed = parse_dxf(str(p))
    assert any(abs(t.height_mm - 2.0) < 0.01 for t in parsed.texts), "текст внутри блока не извлечён"
    assert any(f.rule_code == "ГОСТ 2.304-81" for f in run_checks(parsed)), "2.304 не сработал на тексте в блоке"


def test_realistic_clean_no_false_positives(tmp_path):
    """Реальный стиль (рамка/текст в блоках, штамп без ATTRIB) → 0 ложных critical/major."""
    try:
        from scripts.make_bootstrap_dxf import build_realistic_clean
    except Exception:
        import pytest
        pytest.skip("генератор недоступен")

    p = tmp_path / "realistic.dxf"
    build_realistic_clean().saveas(str(p))
    parsed = parse_dxf(str(p))

    # 1) формат листа распознан как A3 (рамка в блоке + $EXTMAX) — нет ложного 2.301
    assert parsed.frame_size_mm is not None
    w, h = sorted(parsed.frame_size_mm)
    assert abs(w - 297) <= 5 and abs(h - 420) <= 5, parsed.frame_size_mm
    # 2) штамп распознан по подписям, значения граф помечены ненадёжными
    assert parsed.title_block.present and parsed.title_block.values_reliable is False
    # 3) текст из блоков извлечён, высоты в ряду
    assert len(parsed.texts) >= 4, len(parsed.texts)

    findings = run_checks(parsed)
    hard = [f for f in findings if f.severity.value in ("critical", "major")]
    assert not hard, [f"{f.rule_code}:{f.message}" for f in hard]  # ключевое: НЕТ ложных
    # штамп подтверждён, заполненность граф — на ручную (INFO), а не ложный critical
    g2104 = [f for f in findings if f.rule_code == "ГОСТ 2.104-2006"]
    assert len(g2104) == 1 and g2104[0].severity.value == "info", g2104
