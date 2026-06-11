"""Настройки приложения.

План B (dev): SQLite + in-process обработка, без Docker/Redis.
В проде это меняется на PostgreSQL + ARQ/Redis правкой переменных окружения
и одного слоя (app/jobs.py), не трогая бизнес-логику.
"""
from __future__ import annotations

import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Settings:
    def __init__(self) -> None:
        self.secret_key = os.environ.get("SECRET_KEY", "dev-insecure-change-me")
        self.database_url = os.environ.get(
            "DATABASE_URL", f"sqlite+aiosqlite:///{(ROOT / 'normacad.db').as_posix()}"
        )
        self.session_cookie = os.environ.get("SESSION_COOKIE_NAME", "ncad_sid")
        self.session_ttl_days = int(os.environ.get("SESSION_TTL_DAYS", "30"))
        # secure=True для cookie в проде (HTTPS). На localhost http — False,
        # иначе браузер не отдаст cookie и вход сломается. Включается COOKIE_SECURE=1.
        self.cookie_secure = os.environ.get("COOKIE_SECURE", "0").lower() in ("1", "true", "yes")
        # Схема БД: dev — create_all (быстро), прод — Alembic-миграции (USE_ALEMBIC=1).
        self.use_alembic = os.environ.get("USE_ALEMBIC", "0").lower() in ("1", "true", "yes")
        self.storage_dir = Path(os.environ.get("STORAGE_DIR", str(ROOT / "storage")))
        self.max_upload_mb = int(os.environ.get("MAX_UPLOAD_MB", "50"))
        # База ГОСТов (справочник). Путь пользователя; вне репозитория.
        self.gost_db_dir = Path(
            os.environ.get("GOST_DB_DIR", r"C:\Users\elisei\OneDrive\Desktop\БАЗА")
        )
        self.allowed_upload_ext = {".dxf", ".pdf"}  # Этап 4: DXF + вектор-PDF

        # ИИ-чат со справочником по ГОСТ. Бэкенд подключаемый и выбирается по типу
        # пользователя (см. app/chat.py): облако — только individual; bureau/enterprise
        # — только локально. chat_backend задаёт верхний предел для обычных юзеров.
        self.chat_backend = os.environ.get("CHAT_BACKEND", "stub")  # stub | claude | local
        self.anthropic_api_key = os.environ.get("ANTHROPIC_API_KEY", "")
        self.anthropic_model = os.environ.get("ANTHROPIC_MODEL", "claude-sonnet-4-6")
        # Локальная модель для секретного контура (бюро/предприятия): OpenAI-совместимый
        # endpoint (llama.cpp/vLLM/Ollama). Пусто → локального бэкенда нет (офлайн-справочник).
        self.local_chat_url = os.environ.get("LOCAL_CHAT_URL", "")
        self.local_chat_model = os.environ.get("LOCAL_CHAT_MODEL", "local-model")

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024


settings = Settings()
settings.storage_dir.mkdir(parents=True, exist_ok=True)
