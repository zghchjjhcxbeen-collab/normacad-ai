r"""Пакетная калибровка движка на корпусе реальных чертежей (БЛОКЕР №1, PLAN §Этап −1).

Прогоняет все DXF/вектор-PDF из указанных папок/файлов через ТОТ ЖЕ движок, что и сайт
(parse_drawing → run_checks), и печатает по каждому файлу: формат, единицы, габарит рамки,
штамп, тексты/высоты, замечания по важности и предупреждения. Битый файл не валит прогон —
ошибка ловится и печатается. В конце — сводка по корпусу.

Запуск (из корня проекта):
    $env:PYTHONUTF8 = '1'
    .\.venv\Scripts\python.exe scripts\calibrate_corpus.py "C:\путь\к\папке_с_чертежами"
    .\.venv\Scripts\python.exe scripts\calibrate_corpus.py файл1.dxf файл2.pdf --json out.json

Назначение — быстро увидеть, где детект рамки/штампа/текста расходится с реальностью на
чужих CAD (AutoCAD/SolidWorks/nanoCAD/Revit/КОМПАС), и куда чинить движок. Цель калибровки —
precision: 0 ложных critical/major на заведомо корректных листах.
"""
from __future__ import annotations

import json
import pathlib
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.parsing.dispatch import parse_drawing       # noqa: E402
from app.rules.engine import run_checks, summarize   # noqa: E402

SEV_LABEL = {"critical": "ОШИБКА  ", "major": "СЕРЬЁЗНО",
             "minor": "МЕЛКОЕ  ", "info": "ИНФО    "}
EXTS = {".dxf", ".pdf"}


def _gather(paths: list[str]) -> list[pathlib.Path]:
    """Собрать список файлов из папок (рекурсивно) и отдельных файлов."""
    out: list[pathlib.Path] = []
    for raw in paths:
        p = pathlib.Path(raw)
        if p.is_dir():
            out += [f for f in sorted(p.rglob("*")) if f.suffix.lower() in EXTS]
        elif p.is_file() and p.suffix.lower() in EXTS:
            out.append(p)
        else:
            print(f"[!] пропущено (не папка и не DXF/PDF): {raw}")
    return out


def _check_one(path: pathlib.Path) -> dict:
    """Прогнать один файл. Возвращает структурированный результат (для JSON и сводки)."""
    rec: dict = {"file": str(path), "name": path.name, "ok": False}
    print("=" * 78)
    print(f"Файл:    {path.name}")
    try:
        parsed = parse_drawing(str(path))
        findings = run_checks(parsed)
        s = summarize(findings)
    except Exception as exc:  # noqa: BLE001 — битый/неподдерживаемый файл не валит корпус
        print(f"  [ОШИБКА ПАРСИНГА] {type(exc).__name__}: {exc}")
        rec["error"] = f"{type(exc).__name__}: {exc}"
        return rec

    heights = sorted({round(t.height_mm, 2) for t in parsed.texts}) if parsed.texts else []
    tb = parsed.title_block
    counts = s["counts_by_severity"]
    rec.update({
        "ok": True,
        "format": parsed.source_format,
        "units_code": parsed.units_code,
        "units_assumed": parsed.units_assumed,
        "frame_mm": list(parsed.frame_size_mm) if parsed.frame_size_mm else None,
        "stamp_present": bool(tb and tb.present),
        "stamp_values_reliable": bool(tb and tb.values_reliable),
        "font_metrics_reliable": getattr(parsed, "font_metrics_reliable", None),
        "n_texts": len(parsed.texts),
        "heights_mm": heights,
        "line_weights_mm": list(parsed.line_weights_mm),
        "counts": counts,
        "total_findings": s["total_findings"],
        "warnings": list(parsed.warnings),
        "findings": [
            {"code": f.rule_code, "clause": f.rule_clause, "severity": f.severity.value,
             "message": f.message}
            for f in findings
        ],
    })

    units = f"код {parsed.units_code} (x{parsed.mm_per_unit} мм"
    units += ", ПРЕДПОЛОЖЕНЫ)" if parsed.units_assumed else ")"
    print(f"Формат:  {parsed.source_format}")
    print(f"Единицы: {units}")
    print(f"Габарит: {parsed.frame_size_mm} мм")
    if tb and tb.present:
        rel = "значения надёжны" if tb.values_reliable else "значения НЕнадёжны (только наличие)"
        print(f"Штамп:   есть ({rel})")
    else:
        print("Штамп:   НЕТ")
    print(f"Текстов: {len(parsed.texts)}   высоты(мм): {heights}")
    if parsed.line_weights_mm:
        print(f"Толщины линий(мм): {sorted(set(round(w, 2) for w in parsed.line_weights_mm))}")
    for w in parsed.warnings:
        print(f"  [!] {w}")
    print(f"Замечаний: {s['total_findings']}  по важности: {counts}")
    for f in findings:
        label = SEV_LABEL.get(f.severity.value, f.severity.value)
        print(f"  [{label}] {f.rule_code} {f.rule_clause}: {f.message}")
    return rec


def main(argv: list[str]) -> int:
    args = [a for a in argv if not a.startswith("--")]
    json_out = None
    for a in argv:
        if a.startswith("--json"):
            json_out = a.split("=", 1)[1] if "=" in a else (argv[argv.index(a) + 1]
                       if argv.index(a) + 1 < len(argv) else None)
    # значения для --json <path> могли попасть в args — уберём
    if json_out and json_out in args:
        args.remove(json_out)

    if not args:
        print("Использование: calibrate_corpus.py <папка|файл> [...] [--json out.json]")
        print("Пример: calibrate_corpus.py \"C:\\Users\\elisei\\OneDrive\\Desktop\\Новая папка (3)\"")
        return 2

    files = _gather(args)
    if not files:
        print("Не найдено ни одного DXF/PDF по указанным путям.")
        return 1

    results = [_check_one(p) for p in files]

    # ---- сводка по корпусу ----
    print("=" * 78)
    ok = [r for r in results if r.get("ok")]
    errs = [r for r in results if not r.get("ok")]
    clean = [r for r in ok if r["counts"]["critical"] == 0 and r["counts"]["major"] == 0]
    agg = {"critical": 0, "major": 0, "minor": 0, "info": 0}
    fmts: dict[str, int] = {}
    for r in ok:
        for k in agg:
            agg[k] += r["counts"][k]
        fmts[r["format"]] = fmts.get(r["format"], 0) + 1
    print(f"СВОДКА: файлов {len(results)}; распарсено {len(ok)}; ошибок парсинга {len(errs)}")
    print(f"  форматы: {fmts}")
    print(f"  без critical/major: {len(clean)} из {len(ok)}")
    print(f"  суммарно замечаний по важности: {agg}")
    if errs:
        print("  файлы с ошибкой парсинга:")
        for r in errs:
            print(f"    - {r['name']}: {r.get('error')}")
    print("Цель калибровки: 0 ложных critical/major на заведомо корректных листах (precision-first).")

    if json_out:
        pathlib.Path(json_out).write_text(
            json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"JSON-отчёт записан: {json_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
