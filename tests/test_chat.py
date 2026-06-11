"""Тесты ИИ-чата: выбор бэкенда по типу пользователя и ИНВАРИАНТ ПРИВАТНОСТИ.

Главное: бюро/предприятия НИКОГДА не уходят в облако — это проверяется «ловушкой»
(подменяем облачный вызов на исключение и убеждаемся, что он не сработал).
"""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app import chat  # noqa: E402
from app.config import settings  # noqa: E402


def test_resolve_backend_blocks_cloud_for_privileged(monkeypatch):
    """Облако (claude) недопустимо для бюро/предприятия; обычному юзеру — разрешено."""
    monkeypatch.setattr(settings, "chat_backend", "claude")
    monkeypatch.setattr(settings, "local_chat_url", "")
    assert chat.resolve_backend("individual") == "claude"   # обычный юзер → облако ок
    assert chat.resolve_backend("bureau") == "stub"          # облако запрещено, локали нет → офлайн
    assert chat.resolve_backend("enterprise") == "stub"
    # появилась локальная модель → бюро/предприятие идут в неё, не в облако
    monkeypatch.setattr(settings, "local_chat_url", "http://127.0.0.1:8001/v1")
    assert chat.resolve_backend("bureau") == "local"
    assert chat.resolve_backend("enterprise") == "local"
    assert chat.resolve_backend("individual") == "claude"    # обычного юзера это не трогает


def test_resolve_backend_passthrough_non_cloud(monkeypatch):
    """Если глобальный бэкенд не облачный — тип пользователя ничего не сужает."""
    monkeypatch.setattr(settings, "chat_backend", "stub")
    for t in ("individual", "bureau", "enterprise", None):
        assert chat.resolve_backend(t) == "stub"


def test_answer_never_calls_cloud_for_privileged(monkeypatch):
    """ЛОВУШКА приватности: для бюро/предприятия облачный вызов не должен произойти."""
    monkeypatch.setattr(settings, "chat_backend", "claude")
    monkeypatch.setattr(settings, "anthropic_api_key", "sk-MUST-NOT-BE-USED")
    monkeypatch.setattr(settings, "local_chat_url", "")

    def boom(*a, **k):
        raise AssertionError("ОБЛАКО вызвано для бюро/предприятия — нарушение приватности!")

    monkeypatch.setattr(chat, "_answer_claude", boom)
    for t in ("bureau", "enterprise"):
        res = chat.answer("Минимальная высота шрифта по ГОСТ 2.304?", [], org_type=t)
        assert res["backend"] == "stub", res   # офлайн-справочник, облако не тронуто


def test_answer_routes_individual_to_cloud(monkeypatch):
    """Обычный пользователь при настроенном облаке идёт в облако."""
    monkeypatch.setattr(settings, "chat_backend", "claude")
    monkeypatch.setattr(settings, "anthropic_api_key", "sk-test")
    monkeypatch.setattr(chat, "_answer_claude", lambda q, c, h: "ОТВЕТ-ОБЛАКА")
    res = chat.answer("вопрос", [], org_type="individual")
    assert res["backend"] == "claude" and res["answer"] == "ОТВЕТ-ОБЛАКА"


def test_answer_local_calls_openai_compatible_endpoint(monkeypatch):
    """_answer_local шлёт POST на <url>/chat/completions и парсит ответ OpenAI-формата."""
    import httpx

    monkeypatch.setattr(settings, "local_chat_url", "http://127.0.0.1:11434/v1")
    monkeypatch.setattr(settings, "local_chat_model", "qwen2.5")
    captured = {}

    class FakeResp:
        def raise_for_status(self):
            pass

        def json(self):
            return {"choices": [{"message": {"content": "  ЛОКАЛЬНЫЙ ОТВЕТ  "}}]}

    def fake_post(url, json=None, timeout=None):
        captured["url"] = url
        captured["json"] = json
        return FakeResp()

    monkeypatch.setattr(httpx, "post", fake_post)
    out = chat._answer_local("высота шрифта?", "ГОСТ 2.304 — высота прописных…", [])

    assert out == "ЛОКАЛЬНЫЙ ОТВЕТ"  # .strip() применён
    assert captured["url"].endswith("/chat/completions")
    assert captured["json"]["model"] == "qwen2.5"
    msgs = captured["json"]["messages"]
    assert msgs[0]["role"] == "system"  # системный промпт впереди
    assert any("высота шрифта?" in m["content"] for m in msgs)         # вопрос ушёл
    assert any("ГОСТ 2.304" in m["content"] for m in msgs)             # выдержка ушла


def test_answer_privileged_uses_local_not_cloud(monkeypatch):
    """Бюро с поднятой локальной моделью отвечает локально, а облако не зовётся."""
    monkeypatch.setattr(settings, "chat_backend", "claude")
    monkeypatch.setattr(settings, "anthropic_api_key", "sk-MUST-NOT-BE-USED")
    monkeypatch.setattr(settings, "local_chat_url", "http://127.0.0.1:8001/v1")

    def boom(*a, **k):
        raise AssertionError("облако для бюро — нарушение приватности!")

    monkeypatch.setattr(chat, "_answer_claude", boom)
    monkeypatch.setattr(chat, "_answer_local", lambda q, c, h: "ЛОКАЛЬНЫЙ-ОТВЕТ")
    res = chat.answer("вопрос", [], org_type="bureau")
    assert res["backend"] == "local" and res["answer"] == "ЛОКАЛЬНЫЙ-ОТВЕТ"
