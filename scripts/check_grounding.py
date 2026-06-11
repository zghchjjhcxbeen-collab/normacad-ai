r"""Проверка «обоснования по базе»: для каждого правила ЕСКД показать, найден ли
его ГОСТ в базе владельца и какую РЕАЛЬНУЮ формулировку пункта мы оттуда тянем.

Запуск:  $env:PYTHONUTF8='1'; .\.venv\Scripts\python.exe scripts\check_grounding.py

Это инструмент калибровки якорей (RULE_META.ground_anchors) и одновременно
демонстрация инварианта: текст обоснования берётся буквально из базы, не выдумывается.
"""
from __future__ import annotations

import sys

sys.stdout.reconfigure(encoding="utf-8")  # консоль Windows = cp1251 без этого ломается

import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app import gostdb  # noqa: E402
from app.ruleset import RULE_META, RULESET_VERSION  # noqa: E402


def main() -> int:
    if not gostdb.all_gosts():
        print("База ГОСТов недоступна (settings.gost_db_dir). Проверка пропущена.")
        return 0

    print(f"Слой обоснования · ruleset {RULESET_VERSION}\n")
    missing = 0
    for key, m in RULE_META.items():
        code = m["code"]
        in_base = gostdb.by_number(code) is not None
        anchors = m.get("ground_anchors", ())
        q = gostdb.grounding_quote(code, anchors)
        mark = "OK " if (in_base and q) else "!! "
        print(f"[{mark}] правило {key} — {code} ({m['title']})")
        print(f"        в базе: {in_base}")
        if q:
            print(f"        якорь:  «{q['anchor']}»")
            print(f"        цитата: {q['quote']}")
        else:
            print("        цитата: НЕ найдена (показываем только ссылку на стандарт)")
            missing += 1
        print()

    if missing:
        print(f"Внимание: для {missing} правил(а) обоснование из базы не извлеклось — "
              f"подберите ground_anchors в app/ruleset.py.")
    else:
        print("Все P0-правила имеют обоснование, извлечённое прямо из базы ГОСТов.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
