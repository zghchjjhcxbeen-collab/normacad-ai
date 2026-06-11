r"""Проверка связи с ЛОКАЛЬНОЙ моделью чата (Ollama / llama.cpp / vLLM).

Запускать ПОСЛЕ установки модели (см. docs/local-chat-setup.md):

    .\.venv\Scripts\python.exe scripts\check_local_chat.py

Скрипт:
  1) читает LOCAL_CHAT_URL / LOCAL_CHAT_MODEL (из .env, если он есть, иначе из окружения;
     по умолчанию пробует Ollama на 127.0.0.1:11434);
  2) проверяет, что endpoint отвечает и нужная модель загружена;
  3) задаёт реальный вопрос по ГОСТ через тот же код, что и веб-чат (app.chat._answer_local);
  4) подтверждает инвариант приватности: bureau/enterprise → local, НЕ облако.

Никакой код продукта не меняется — это только диагностика.
"""
from __future__ import annotations

import os
import pathlib
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def _load_dotenv() -> None:
    """Подхватить переменные из .env без сторонних зависимостей (до импорта config)."""
    env = ROOT / ".env"
    if not env.exists():
        return
    for raw in env.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if line and not line.startswith("#") and "=" in line:
            key, val = line.split("=", 1)
            os.environ.setdefault(key.strip(), val.strip())


_load_dotenv()
# Удобный дефолт: если URL не задан — пробуем стандартный Ollama.
os.environ.setdefault("LOCAL_CHAT_URL", "http://127.0.0.1:11434/v1")
os.environ.setdefault("LOCAL_CHAT_MODEL", "qwen2.5:7b")

import httpx  # noqa: E402

from app import chat, gostdb  # noqa: E402
from app.config import settings  # noqa: E402

SEP = "-" * 64


def main() -> int:
    url = settings.local_chat_url.rstrip("/")
    model = settings.local_chat_model
    print(SEP)
    print(f"LOCAL_CHAT_URL   = {url}")
    print(f"LOCAL_CHAT_MODEL = {model}")
    print(f"CHAT_BACKEND     = {settings.chat_backend}")
    print(SEP)

    # 1) Endpoint жив? Список моделей (OpenAI-совместимый GET /models).
    try:
        r = httpx.get(url + "/models", timeout=5.0)
        r.raise_for_status()
    except httpx.ConnectError:
        print("✗ Сервер модели НЕ отвечает (соединение отклонено).")
        print("  • Установлена ли Ollama? См. docs/local-chat-setup.md")
        print("  • Запущена ли она? В PowerShell: ollama list")
        print(f"  • Верный ли порт в LOCAL_CHAT_URL? Сейчас: {url}")
        return 1
    except Exception as exc:  # noqa: BLE001
        print(f"✗ Endpoint /models вернул ошибку: {type(exc).__name__}: {exc}")
        print("  Это может быть нормально для llama.cpp/vLLM — проверим генерацию ниже.")
        ids = []
    else:
        ids = [m.get("id", "") for m in r.json().get("data", [])]
        print(f"✓ Endpoint отвечает. Доступно моделей: {len(ids)}")
        if ids:
            print("  модели:", ", ".join(ids[:12]) + (" …" if len(ids) > 12 else ""))
        base = model.split(":", 1)[0]
        if ids and not any(m == model or m.split(":", 1)[0] == base for m in ids):
            print(f"⚠ Модель '{model}' не видна в списке. Загрузите её:")
            print(f"    ollama pull {model}")

    # 2) Реальный вопрос по ГОСТ — ровно тем же кодом, что и веб-чат.
    question = "Какая минимальная высота шрифта на чертеже по ГОСТ 2.304?"
    print(SEP)
    print(f"Вопрос: {question}")
    _, context, citations = chat._retrieve(question)  # выдержки из базы ГОСТ (как в проде)
    if not context:
        print("⚠ База ГОСТ не дала контекста (проверьте GOST_DB_DIR) — модель ответит без выдержек.")
    try:
        answer = chat._answer_local(question, context, [])
    except httpx.HTTPStatusError as exc:
        print(f"✗ Модель вернула HTTP-ошибку: {exc.response.status_code}")
        body = exc.response.text[:300]
        print(f"  тело: {body}")
        if exc.response.status_code == 404:
            print(f"  Похоже, модель '{model}' не установлена. Выполните: ollama pull {model}")
        return 1
    except Exception as exc:  # noqa: BLE001
        print(f"✗ Ошибка обращения к модели: {type(exc).__name__}: {exc}")
        return 1

    print(SEP)
    print("✓ ОТВЕТ ЛОКАЛЬНОЙ МОДЕЛИ:")
    print(answer)
    if citations:
        print("\nИсточники (из базы ГОСТ):", ", ".join(c["code"] for c in citations))

    # 3) Инвариант приватности: для бюро/предприятия облако недоступно в принципе.
    print(SEP)
    saved = settings.chat_backend
    settings.chat_backend = "claude"  # даже если глобально настроено облако…
    try:
        routed = {t: chat.resolve_backend(t) for t in ("individual", "bureau", "enterprise")}
    finally:
        settings.chat_backend = saved
    print("Маршрутизация бэкенда при CHAT_BACKEND=claude и поднятой локальной модели:")
    for t, b in routed.items():
        mark = "облако" if b == "claude" else ("ЛОКАЛЬНО" if b == "local" else b)
        print(f"  {t:11s} → {b}  ({mark})")
    assert routed["bureau"] != "claude" and routed["enterprise"] != "claude", \
        "НАРУШЕНИЕ ПРИВАТНОСТИ: бюро/предприятие не должны идти в облако!"
    print("✓ Инвариант соблюдён: бюро и предприятия в облако не уходят.")
    print(SEP)
    print("ГОТОВО. Локальная модель подключена и отвечает.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
