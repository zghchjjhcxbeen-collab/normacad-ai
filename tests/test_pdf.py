"""Тесты вектор-PDF тракта (Этап 4). Фикстуры генерируются в tmp на лету (fpdf2)."""
from __future__ import annotations

import os
import pathlib
import sys

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.parsing.pdf import classify_pdf, parse_pdf  # noqa: E402
from app.rules.engine import run_checks  # noqa: E402

try:
    from scripts.make_bootstrap_pdf import build_clean, build_dirty  # noqa: E402
    _HAVE_FPDF = True
except Exception:  # fpdf2 не установлен
    _HAVE_FPDF = False


def test_looks_like_scale_filters_non_scales():
    """Анти-ложняк 2.302 на PDF: отсекаем даты/номера/время, принимаем масштабы ЕСКД."""
    from app.parsing.pdf import _looks_like_scale
    assert _looks_like_scale("1:2")
    assert _looks_like_scale("2,5:1")
    assert _looks_like_scale("1:1000")
    assert not _looks_like_scale("2024:01")   # год:месяц
    assert not _looks_like_scale("12:30")      # время
    assert not _looks_like_scale("1:5000")     # вне диапазона ряда


def test_cap_ratio_known_vs_unknown():
    """cap-ratio: известный шрифт надёжен, неизвестный — нет (тогда 2.304 → ручная)."""
    from app.parsing.pdf import _cap_ratio
    r, ok = _cap_ratio("Helvetica")
    assert ok and 0.70 <= r <= 0.73
    r, ok = _cap_ratio("ABCDEF+Arial-Bold")   # subset-префикс отбрасывается
    assert ok
    r, ok = _cap_ratio("SomeWeirdEmbeddedFont")
    assert not ok and r == 0.70
    r, ok = _cap_ratio(None)
    assert not ok


def test_cap_ratio_prefers_embedded_descriptor():
    """Реальный CapHeight из дескриптора важнее усреднённой таблицы; шрифт без обоих → ненадёжен."""
    from app.parsing.pdf import _cap_ratio
    # ISOCP в таблице = 0.700, но дескриптор даёт точные 0.642 → берём дескриптор
    r, ok = _cap_ratio("ABCD+ISOCPEUR", {"isocpeur": 0.642})
    assert ok and abs(r - 0.642) < 1e-9, (r, ok)
    # шрифт, которого нет ни в дескрипторах, ни в таблице → метрика ненадёжна
    r, ok = _cap_ratio("WeirdEmbeddedFont", {"isocpeur": 0.642})
    assert not ok, (r, ok)


def test_embedded_capheight_makes_metric_reliable(tmp_path):
    """Встроенный TTF с CapHeight → метрика надёжна даже без шрифта в таблице (end-to-end)."""
    pytest.importorskip("fpdf")
    font = r"C:\Windows\Fonts\arial.ttf"
    if not os.path.exists(font):
        pytest.skip("нет системного TTF для embedded-теста")
    from fpdf import FPDF
    from app.parsing.pdf import _embedded_cap_ratios, parse_pdf

    pdf = FPDF(orientation="L", unit="mm", format="A3")
    pdf.add_page()
    pdf.add_font("emb", fname=font)
    pdf.set_font("emb", size=14)
    pdf.rect(10, 10, pdf.w - 20, pdf.h - 20)
    pdf.text(20, 30, "EMBEDDED CAPHEIGHT TEST 1234567890")
    p = tmp_path / "emb.pdf"
    pdf.output(str(p))

    ratios = _embedded_cap_ratios(str(p))
    assert ratios, "должен прочитаться хотя бы один CapHeight из дескриптора"
    assert all(0.5 < r < 0.9 for r in ratios.values()), ratios  # правдоподобная доля
    parsed = parse_pdf(str(p))
    assert parsed.font_metrics_reliable is True  # надёжно по дескриптору, не по таблице


def test_pdf_unreliable_fonts_go_manual():
    """Гейт: при ненадёжной метрике шрифта 2.304 НЕ проверяется (ручная INFO), без ложняка."""
    from app.domain import ParsedDrawing, TextItem
    parsed = ParsedDrawing(
        source_path="x.pdf", units_code=-1, units_assumed=False, mm_per_unit=0.3528,
        frame_size_mm=(420, 297),
        texts=[TextItem("x", height_mm=1.0, insert_mm=(0.0, 0.0), handle="w0")],
        source_format="vector_pdf", font_metrics_reliable=False,
    )
    g2304 = [f for f in run_checks(parsed) if "2.304" in f.rule_code]
    assert len(g2304) == 1 and g2304[0].severity.value == "info", g2304  # ручная, не major


@pytest.mark.skipif(not _HAVE_FPDF, reason="fpdf2 недоступен")
def test_clean_pdf_no_major(tmp_path):
    p = tmp_path / "clean.pdf"
    build_clean(str(p))
    assert classify_pdf(str(p)) == "vector"
    parsed = parse_pdf(str(p))
    assert parsed.source_format == "vector_pdf"
    assert parsed.frame_size_mm is not None
    assert parsed.font_metrics_reliable is True  # Helvetica — известная метрика
    findings = run_checks(parsed)
    majors = [f for f in findings if f.severity.value in ("critical", "major")]
    assert not majors, [f.message for f in majors]
    codes = {f.rule_code for f in findings}
    # формат A3 и масштаб 1:2 — ок; шрифт 14pt → cap-height ≈3,55 мм в ряду → 2.304 БЕЗ замечания;
    # 2.104 (таблица штампа) на PDF — ручная проверка (INFO).
    assert not any("2.304" in c for c in codes), f"2.304 должен пройти на clean: {codes}"
    assert any("2.104" in c for c in codes), codes


@pytest.mark.skipif(not _HAVE_FPDF, reason="fpdf2 недоступен")
def test_dirty_pdf_flags_format_and_scale(tmp_path):
    p = tmp_path / "dirty.pdf"
    build_dirty(str(p))
    parsed = parse_pdf(str(p))
    assert parsed.source_format == "vector_pdf"
    findings = run_checks(parsed)
    majors = {f.rule_code for f in findings if f.severity.value == "major"}
    assert any("2.301" in c for c in majors), majors
    assert any("2.302" in c for c in majors), majors
    # мелкий 6pt текст → cap-height ≈1,52 мм < 2,5 → 2.304 теперь ловится (а не «ручная»)
    assert any("2.304" in c for c in majors), majors
