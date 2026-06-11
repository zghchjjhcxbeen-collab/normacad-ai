"""Сквозной smoke-тест веб-слоя без запуска сервера (FastAPI TestClient).

Регистрация -> загрузка clean/dirty DXF -> обработка (BackgroundTasks выполняются
в рамках запроса под TestClient) -> проверка содержимого отчёта.
"""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# свежая БД для воспроизводимости
_db = ROOT / "normacad.db"
if _db.exists():
    _db.unlink()

from fastapi.testclient import TestClient  # noqa: E402

from app import gostdb  # noqa: E402
from app.main import app  # noqa: E402

BOOTSTRAP = ROOT / "data" / "bootstrap"


def run() -> None:
    with TestClient(app) as c:
        # CSRF (Этап 1): получить токен GET-ом формы, слать его во всех POST.
        c.get("/register")
        csrf = c.cookies.get("ncad_csrf")
        assert csrf, "ожидалась CSRF-cookie после GET /register"

        # POST без токена должен быть отклонён (403) — проверяем, что защита работает.
        nr = c.post("/register", data={"email": "x@y.z", "password": "password123"},
                    follow_redirects=False)
        assert nr.status_code == 403, ("CSRF: POST без токена должен быть 403", nr.status_code)

        r = c.post("/register", data={"email": "smoke@test.local", "password": "password123",
                                      "csrf_token": csrf}, follow_redirects=False)
        assert r.status_code == 303, (r.status_code, r.text[:400])

        results: dict[str, str] = {}
        for fname in ("clean_a3.dxf", "dirty_a3.dxf"):
            with open(BOOTSTRAP / fname, "rb") as fh:
                r = c.post("/uploads", files={"file": (fname, fh, "application/dxf")},
                           data={"csrf_token": csrf}, follow_redirects=False)
            assert r.status_code == 303, (r.status_code, r.text[:400])
            job_id = r.headers["location"].rsplit("/", 1)[1]
            fr = c.get(f"/jobs/{job_id}/fragment")
            assert fr.status_code == 200, fr.status_code
            results[fname] = fr.text
            print(f"--- {fname} (job {job_id}) ---")
            print("  отчёт готов:", "Отчёт" in fr.text)
            print("  есть SVG:   ", "<svg" in fr.text)
            for code in ("2.301", "2.302", "2.304", "2.104"):
                print(f"  ГОСТ {code}:  ", code in fr.text)

        # ИИ-чат (stub-бэкенд + поиск по базе ГОСТов)
        assert c.get("/chat").status_code == 200
        cr = c.post("/chat", data={"question": "какая минимальная высота чертёжного шрифта по госту?",
                                   "csrf_token": csrf}, follow_redirects=False)
        assert cr.status_code == 200, cr.status_code
        assert "2.304" in cr.text, "чат должен сослаться на ГОСТ 2.304"
        print("\n--- чат (stub) ---")
        print("  цитата ГОСТ 2.304:", "2.304" in cr.text)

        print("\nhealth:", c.get("/api/health").json())

        assert "Замечаний не найдено" in results["clean_a3.dxf"], "clean должен быть без замечаний"
        dirty = results["dirty_a3.dxf"]
        for code in ("2.301", "2.302", "2.304", "2.104"):
            assert code in dirty, f"dirty: ожидался {code}"
        assert "<svg" in dirty, "ожидался SVG-просмотрщик"

        # обоснование из базы ГОСТ (только если база доступна на этой машине)
        print("\n--- обоснование из базы ГОСТ ---")
        if gostdb.all_gosts():
            assert "Что говорит ГОСТ" in dirty, "ожидался блок обоснования из базы"
            assert any(s in dirty for s in ("внешней рамки", "прописных", "линейного размера")), \
                "ожидалась реальная цитата пункта из базы"
            print("  блок обоснования + реальная цитата: True")
        else:
            print("  пропущено (база ГОСТов недоступна)")

        # --- вектор-PDF (Этап 4): через реальные роуты ---
        print("\n--- вектор-PDF ---")
        pdf_results: dict[str, str] = {}
        for fname in ("clean_a3.pdf", "dirty_a3.pdf"):
            fp = BOOTSTRAP / fname
            if not fp.exists():
                print(f"  {fname}: фикстура отсутствует — пропуск (scripts/make_bootstrap_pdf.py)")
                continue
            with open(fp, "rb") as fh:
                r = c.post("/uploads", files={"file": (fname, fh, "application/pdf")},
                           data={"csrf_token": csrf}, follow_redirects=False)
            assert r.status_code == 303, (r.status_code, r.text[:400])
            job_id = r.headers["location"].rsplit("/", 1)[1]
            fr = c.get(f"/jobs/{job_id}/fragment")
            assert fr.status_code == 200, fr.status_code
            pdf_results[fname] = fr.text
            print(f"  {fname}: отчёт={'Отчёт' in fr.text}, SVG={'<svg' in fr.text}, "
                  f"2.301={'2.301' in fr.text}, 2.302={'2.302' in fr.text}, "
                  f"2.304={'2.304' in fr.text}")
        if "dirty_a3.pdf" in pdf_results:
            d2 = pdf_results["dirty_a3.pdf"]
            assert "2.301" in d2 and "2.302" in d2, "dirty PDF: ожидались 2.301 и 2.302"
            # cap-height: мелкий 6pt текст → 2.304 теперь ловится на PDF (не «ручная»)
            assert "2.304" in d2, "dirty PDF: ожидалось замечание 2.304 (мелкий шрифт)"
        if "clean_a3.pdf" in pdf_results:
            # 2.304 на clean проходит (14pt в ряду); ручная остаётся по 2.104 (таблица штампа)
            assert "ручной проверки" in pdf_results["clean_a3.pdf"], \
                "clean PDF: ожидалась пометка ручной проверки (2.104)"

        print("\nSMOKE OK")


if __name__ == "__main__":
    run()
