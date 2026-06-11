"""Сборка всех проверок в один прогон.

На Этапе 0 — простой список. Позже сюда добавятся: эскалация в vision,
маркировка source/confidence, дедуп пересекающихся находок (PLAN.md, Stage 4).
"""
from __future__ import annotations

from app.domain import Finding, ParsedDrawing, Severity, Source
from app.rules.checks import (
    check_format_2301,
    check_fonts_2304,
    check_lines_2303,
    check_scale_2302,
    check_title_block_2104,
)
from app.ruleset import RULE_META, RULESET_VERSION

RULES = [
    check_format_2301,
    check_scale_2302,
    check_fonts_2304,
    check_lines_2303,
    check_title_block_2104,
]

_SEVERITY_ORDER = {
    Severity.CRITICAL: 0,
    Severity.MAJOR: 1,
    Severity.MINOR: 2,
    Severity.INFO: 3,
}


def _manual_pdf_note(key: str, reason: str) -> Finding:
    """INFO «требует ручной проверки» для правила, которое на этом PDF не проверяем."""
    m = RULE_META[key]
    return Finding(
        m["code"], m["clause"], m["title"], Severity.INFO,
        f"На вектор-PDF «{m['title'].lower()}» автоматически не проверяется ({reason}) — "
        f"требует ручной проверки нормоконтролёром.",
        fix="Проверьте этот пункт вручную или загрузите DXF для автоматической проверки.",
        source=Source.DETERMINISTIC,
    )


def _raster_note(parsed: ParsedDrawing) -> Finding:
    msg = parsed.warnings[0] if parsed.warnings else (
        "Скан/растровый PDF — растровый тракт ещё не подключён."
    )
    return Finding(
        "PDF", "", "Тип файла", Severity.INFO, msg,
        fix="Загрузите вектор-PDF (born-digital) или DXF.",
        source=Source.DETERMINISTIC,
    )


def run_checks(parsed: ParsedDrawing) -> list[Finding]:
    """Прогнать проверки по формату источника и вернуть отсортированные находки."""
    fmt = getattr(parsed, "source_format", "dxf")
    if fmt == "raster_pdf":
        return [_raster_note(parsed)]

    findings: list[Finding] = []
    if fmt == "vector_pdf":
        findings += check_format_2301(parsed)
        findings += check_scale_2302(parsed)
        # 2.304: cap-height измерима только при известной метрике шрифта (parsing/pdf.py).
        if getattr(parsed, "font_metrics_reliable", False) and parsed.texts:
            findings += check_fonts_2304(parsed)
        else:
            findings.append(_manual_pdf_note("2.304", "метрика шрифта неизвестна"))
        # 2.104: надёжная реконструкция таблицы штампа на PDF — отдельный шаг (нужен корпус).
        findings.append(_manual_pdf_note("2.104", "нужна реконструкция таблицы штампа"))
    else:  # dxf
        for rule in RULES:
            findings.extend(rule(parsed))
    findings.sort(key=lambda f: _SEVERITY_ORDER.get(f.severity, 9))
    return findings


def summarize(findings: list[Finding]) -> dict:
    """Сводка для отчёта (PLAN.md, Stage 5)."""
    counts = {s.value: 0 for s in Severity}
    for f in findings:
        counts[f.severity.value] += 1
    return {
        "ruleset_version": RULESET_VERSION,
        "total_findings": len(findings),
        "counts_by_severity": counts,
    }
