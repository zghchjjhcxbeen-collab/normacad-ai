"""ИИ-чат: ответ на вопрос клиента с опорой на базу ГОСТов (RAG).

Бэкенд выбирается ПО ТИПУ ПОЛЬЗОВАТЕЛЯ (решение владельца — приватность важнее охвата):
  • individual (обычный пользователь) — допустимо облако Claude, если оно настроено;
  • bureau / enterprise (бюро, предприятие) — НИКОГДА не уходят в облако (их КД может быть
    режимной): только локальная модель (LOCAL_CHAT_URL) или офлайн-справочник (stub).

Инвариант приватности живёт в `resolve_backend`: глобальная настройка settings.chat_backend
задаёт верхний предел для обычных юзеров, а для бюро/предприятий может лишь СУЗИТЬ выбор —
открыть облако она им не может. В облако/локальную модель уходят только короткие выдержки
из ГОСТов, не целые тексты (копирайт). Бэкенды:
  stub   — без модели: находит стандарты и показывает выдержку (dev/офлайн без модели);
  claude — облачный Anthropic (ANTHROPIC_API_KEY + пакет anthropic);
  local  — локальная OpenAI-совместимая модель (LOCAL_CHAT_URL): запрос не покидает периметр.
"""
from __future__ import annotations

from app import gostdb
from app.config import settings

# Бэкенды, отправляющие данные во ВНЕШНЕЕ облако (недопустимы для бюро/предприятий).
CLOUD_BACKENDS = frozenset({"claude"})
# Типы пользователей, которым облако запрещено полностью.
OFFLINE_ONLY_TYPES = frozenset({"bureau", "enterprise"})

SYSTEM_PROMPT = (
    "Ты — ассистент по нормоконтролю конструкторской документации (ЕСКД/ГОСТ). "
    "Отвечай кратко и по делу на русском. Опирайся ТОЛЬКО на приведённые ниже выдержки из ГОСТов; "
    "если их недостаточно — честно скажи об этом и не выдумывай требования. "
    "Всегда указывай номер ГОСТа и, по возможности, пункт."
)


def resolve_backend(org_type: str | None) -> str:
    """Допустимый бэкенд чата для типа пользователя — единственная точка инварианта приватности.

    Бюро/предприятие + облачный бэкенд → принудительно локальная модель (если поднята
    LOCAL_CHAT_URL) или офлайн-справочник (stub). Облако им не отдаём НИКОГДА.
    Обычный пользователь — как настроено глобально (settings.chat_backend).
    """
    backend = settings.chat_backend
    if org_type in OFFLINE_ONLY_TYPES and backend in CLOUD_BACKENDS:
        return "local" if settings.local_chat_url else "stub"
    return backend


def _retrieve(question: str) -> tuple[list[dict], str, list[dict]]:
    hits = gostdb.search(question, limit=3)
    context_parts, citations = [], []
    for g in hits:
        excerpt = gostdb.read_excerpt(g, max_chars=900)
        context_parts.append(f"{g['number']} — {g['title']}\n{excerpt}")
        citations.append({"code": g["number"], "title": g["title"]})
    return hits, "\n\n---\n\n".join(context_parts), citations


def answer(
    question: str,
    history: list[dict] | None = None,
    org_type: str | None = "individual",
) -> dict:
    """Ответить на вопрос, выбрав бэкенд по типу пользователя (см. resolve_backend)."""
    hits, context, citations = _retrieve(question)
    backend = resolve_backend(org_type)

    if backend == "claude" and settings.anthropic_api_key:
        try:
            text = _answer_claude(question, context, history or [])
            return {"answer": text, "citations": citations, "backend": "claude"}
        except Exception as exc:  # noqa: BLE001
            return {
                "answer": f"Не удалось обратиться к модели ({exc}). Найденные стандарты — ниже.",
                "citations": citations, "backend": "claude-error",
            }

    if backend == "local" and settings.local_chat_url:
        try:
            text = _answer_local(question, context, history or [])
            return {"answer": text, "citations": citations, "backend": "local"}
        except Exception as exc:  # noqa: BLE001
            return {
                "answer": f"Локальная модель недоступна ({exc}). Найденные стандарты — ниже:\n\n"
                          + _answer_stub(hits),
                "citations": citations, "backend": "local-error",
            }

    return {"answer": _answer_stub(hits), "citations": citations, "backend": "stub"}


def _answer_stub(hits: list[dict]) -> str:
    """Офлайн-ответ из базы ГОСТ (без генеративной модели): релевантный стандарт + выдержка.

    Используется как основной режим без модели и как фоллбэк, если локальная модель/облако
    недоступны. Числовые требования берутся человеком из текста стандарта, не выдумываются.
    """
    if not hits:
        return (
            "По базе ГОСТов не нашлось подходящего стандарта. Уточните формулировку "
            "или укажите номер ГОСТа (например, 2.304 — шрифты, 2.301 — форматы)."
        )
    top = hits[0]
    lines = [f"По вашему вопросу наиболее релевантен {top['number']} — {top['title']}."]
    excerpt = gostdb.read_excerpt(top, max_chars=600)
    if excerpt:
        lines += ["", "Выдержка из стандарта:", excerpt]
    if len(hits) > 1:
        lines += ["", "Также может быть полезно:"]
        lines += [f"• {g['number']} — {g['title']}" for g in hits[1:]]
    lines += [
        "",
        "Ответ собран из базы ГОСТ (офлайн-режим, без генеративной модели). "
        "Точные числовые требования сверяйте по тексту стандарта.",
    ]
    return "\n".join(lines)


def _build_messages(question: str, context: str, history: list[dict]) -> list[dict]:
    """Сообщения для chat-completions: история + вопрос с выдержками ГОСТ (только выдержки)."""
    messages: list[dict] = []
    for h in history[-6:]:
        if h.get("role") in ("user", "assistant") and h.get("content"):
            messages.append({"role": h["role"], "content": h["content"]})
    messages.append({
        "role": "user",
        "content": f"Вопрос: {question}\n\nВыдержки из ГОСТов (справочник):\n{context or '(не найдено)'}",
    })
    return messages


def _answer_local(question: str, context: str, history: list[dict]) -> str:
    """Локальная модель через OpenAI-совместимый endpoint (LOCAL_CHAT_URL).

    Для бюро/предприятий: запрос НЕ покидает периметр клиента. Совместимо с
    llama.cpp / vLLM / Ollama (/v1/chat/completions). В контекст идут только выдержки ГОСТ.
    """
    import httpx  # httpx уже в зависимостях (используется и тест-клиентом)

    messages = [{"role": "system", "content": SYSTEM_PROMPT}, *_build_messages(question, context, history)]
    url = settings.local_chat_url.rstrip("/") + "/chat/completions"
    resp = httpx.post(
        url,
        json={"model": settings.local_chat_model, "messages": messages,
              "max_tokens": 700, "temperature": 0.2},
        timeout=60.0,
    )
    resp.raise_for_status()
    return resp.json()["choices"][0]["message"]["content"].strip()


def _answer_claude(question: str, context: str, history: list[dict]) -> str:
    import anthropic  # импорт здесь: пакет не нужен для stub-бэкенда

    client = anthropic.Anthropic(api_key=settings.anthropic_api_key)
    resp = client.messages.create(
        model=settings.anthropic_model,
        max_tokens=700,
        system=SYSTEM_PROMPT,
        messages=_build_messages(question, context, history),
    )
    return "".join(b.text for b in resp.content if getattr(b, "type", "") == "text").strip()
