"""Тесты движка правил на сконструированных ParsedDrawing (без файлового ввода).

Проверяют центральное обещание Этапа 0: чистый чертёж -> 0 замечаний,
«грязный» -> срабатывают все 4 правила P0, отсутствие штампа -> critical.
"""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.domain import ParsedDrawing, Severity, TextItem, TitleBlock  # noqa: E402
from app.rules.checks import match_format, normalize_scale  # noqa: E402
from app.rules.engine import run_checks  # noqa: E402


def _clean() -> ParsedDrawing:
    return ParsedDrawing(
        source_path="x", units_code=4, units_assumed=False, mm_per_unit=1.0,
        frame_size_mm=(420, 297),
        texts=[TextItem("a", 3.5, (0, 0), "1"), TextItem("b", 5.0, (0, 0), "2")],
        title_block=TitleBlock(
            present=True,
            attribs={"НАИМЕНОВАНИЕ": "Вал", "ОБОЗНАЧЕНИЕ": "АБВГ.301261.001", "МАСШТАБ": "1:1"},
            bbox_mm=(235, 0, 185, 55), block_handle="TB",
        ),
    )


def _dirty() -> ParsedDrawing:
    return ParsedDrawing(
        source_path="x", units_code=4, units_assumed=False, mm_per_unit=1.0,
        frame_size_mm=(400, 300),
        texts=[TextItem("c", 2.0, (0, 0), "9")],
        title_block=TitleBlock(
            present=True,
            attribs={"НАИМЕНОВАНИЕ": "Кронштейн", "ОБОЗНАЧЕНИЕ": "", "МАСШТАБ": "1:3"},
            bbox_mm=(215, 0, 185, 55), block_handle="TB",
        ),
    )


def test_normalize_scale():
    assert normalize_scale("М 1:2,5") == "1:2.5"
    assert normalize_scale("1 : 1") == "1:1"
    assert normalize_scale("масштаб 2:1") == "2:1"
    assert normalize_scale("чушь") is None
    assert normalize_scale("") is None


def test_match_format():
    assert match_format(420, 297, 5) == "A3"
    assert match_format(297, 420, 5) == "A3"   # ориентация не важна
    assert match_format(210, 297, 5) == "A4"
    assert match_format(400, 300, 5) is None


def test_clean_drawing_has_no_findings():
    assert run_checks(_clean()) == []


def test_dirty_drawing_fires_all_four_rules():
    codes = {f.rule_code for f in run_checks(_dirty())}
    assert "ГОСТ 2.301-68" in codes      # формат
    assert "ГОСТ 2.302-68" in codes      # масштаб
    assert "ГОСТ 2.304-81" in codes      # шрифт
    assert "ГОСТ 2.104-2006" in codes    # штамп


def test_missing_title_block_is_critical():
    d = _clean()
    d.title_block = TitleBlock(present=False)
    findings = run_checks(d)
    assert any(
        f.rule_code == "ГОСТ 2.104-2006" and f.severity == Severity.CRITICAL
        for f in findings
    )


def test_every_finding_has_fix():
    for finding in run_checks(_dirty()):
        assert finding.fix, f"нет рекомендации «как исправить» для {finding.rule_code}"


def test_line_thickness_2303():
    d = _clean()
    d.line_weights_mm = [0.25, 0.5]   # основная 0.5 мм — в норме
    assert not any(f.rule_code == "ГОСТ 2.303-68" for f in run_checks(d))
    d.line_weights_mm = [0.5, 2.0]    # основная 2.0 мм — вне 0,5–1,4
    assert any(f.rule_code == "ГОСТ 2.303-68" for f in run_checks(d))
    d.line_weights_mm = []            # толщины не заданы — правило молчит
    assert not any(f.rule_code == "ГОСТ 2.303-68" for f in run_checks(d))
