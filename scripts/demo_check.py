"""Демо: прогнать ядро нормоконтроля по бутстрап-набору и напечатать отчёт.

Запуск (из корня проекта):
    python scripts/make_bootstrap_dxf.py   # сначала сгенерировать файлы
    python scripts/demo_check.py
"""
from __future__ import annotations

import pathlib
import sys

# Windows-консоль часто в cp1251 — принудительно выводим UTF-8, иначе падаем на «×» и кириллице.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.parsing.dxf import parse_dxf          # noqa: E402
from app.rules.engine import run_checks, summarize  # noqa: E402

SEV_LABEL = {
    "critical": "ОШИБКА  ",
    "major": "СЕРЬЁЗНО",
    "minor": "МЕЛКОЕ  ",
    "info": "ИНФО    ",
}


def check_file(path: pathlib.Path) -> int:
    parsed = parse_dxf(str(path))
    findings = run_checks(parsed)
    s = summarize(findings)

    print("=" * 72)
    print(f"Файл:    {path.name}")
    units = f"код {parsed.units_code} (x{parsed.mm_per_unit} мм"
    units += ", ПРЕДПОЛОЖЕНЫ)" if parsed.units_assumed else ")"
    print(f"Единицы: {units}")
    print(f"Габарит: {parsed.frame_size_mm} мм")
    tb = parsed.title_block
    print(f"Штамп:   {'есть' if tb and tb.present else 'НЕТ'}")
    print(f"Текстов: {len(parsed.texts)}")
    for w in parsed.warnings:
        print(f"  [!] {w}")
    print(f"Замечаний: {s['total_findings']}  по важности: {s['counts_by_severity']}")
    for f in findings:
        loc = f.handle or (f"bbox {f.bbox}" if f.bbox else "-")
        label = SEV_LABEL.get(f.severity.value, f.severity.value)
        print(f"  [{label}] {f.rule_code} {f.rule_clause}: {f.message}  (место: {loc})")
    return s["total_findings"]


def main() -> None:
    d = ROOT / "data" / "bootstrap"
    files = sorted(d.glob("*.dxf"))
    if not files:
        print("Нет DXF. Сначала: python scripts/make_bootstrap_dxf.py")
        return
    for p in files:
        check_file(p)
    print("=" * 72)


if __name__ == "__main__":
    main()
