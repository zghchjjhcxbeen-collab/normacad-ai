r"""Залить файлы в локально запущенный сайт под демо-юзером (для демо/теста).

Удобно, чтобы быстро положить чертежи в кабинет и посмотреть отчёт с подсветкой,
не перетаскивая их вручную. Сайт должен быть запущен (uvicorn на 127.0.0.1:8000).

Usage:
    .\.venv\Scripts\python.exe scripts\upload_to_site.py <файл> [<файл> ...]
    .\.venv\Scripts\python.exe scripts\upload_to_site.py C:\samples\a.dxf --email smoke@test.local --password password123
"""
from __future__ import annotations

import re
import sys
import time

import httpx

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def main(argv: list[str]) -> int:
    files = [a for a in argv if not a.startswith("--")]
    opts = {"--email": "smoke@test.local", "--password": "password123",
            "--base": "http://127.0.0.1:8000"}
    for key in opts:
        if key in argv:
            i = argv.index(key)
            if i + 1 < len(argv):
                opts[key] = argv[i + 1]
                if opts[key] in files:
                    files.remove(opts[key])
    if not files:
        print("Укажите хотя бы один файл (.dxf/.pdf). См. шапку скрипта.")
        return 2

    base = opts["--base"]
    c = httpx.Client(base_url=base, follow_redirects=True, timeout=90)
    try:
        c.get("/login")
        csrf = c.cookies.get("ncad_csrf")
        r = c.post("/login", data={"email": opts["--email"],
                                   "password": opts["--password"], "csrf_token": csrf})
        if "/dashboard" not in str(r.url):
            print(f"Не удалось войти ({opts['--email']}). Сайт запущен? Верны ли логин/пароль?")
            return 1
        for path in files:
            name = path.replace("\\", "/").split("/")[-1]
            try:
                blob = open(path, "rb")
            except OSError as exc:
                print(f"{name}: не открыть файл — {exc}")
                continue
            with blob:
                csrf = c.cookies.get("ncad_csrf")
                up = c.post("/uploads", files={"file": (name, blob, "application/octet-stream")},
                            data={"csrf_token": csrf})
            m = re.search(r"/jobs/(\d+)", str(up.url))
            if not m:
                print(f"{name}: загрузка отклонена (code={up.status_code}). "
                      "Поддерживаются .dxf и вектор-PDF.")
                continue
            jid = m.group(1)
            verdict = "не дошёл до готовности"
            for _ in range(60):
                t = c.get(f"/jobs/{jid}/fragment").text
                if "Замечания ИИ" in t:
                    n = len(re.findall(r'id="finding-\d+"', t))
                    verdict = f"готово, замечаний ИИ: {n}  →  {base}/jobs/{jid}"
                    break
                if "Не удалось обработать" in t:
                    verdict = "ОШИБКА обработки"
                    break
                time.sleep(0.5)
            print(f"{name}: {verdict}")
    finally:
        c.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
