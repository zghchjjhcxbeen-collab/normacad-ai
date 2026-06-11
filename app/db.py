"""Асинхронный движок БД и сессии SQLAlchemy.

Этап 0/План B — SQLite через aiosqlite. Схема создаётся двумя путями:
  • dev (по умолчанию): Base.metadata.create_all — быстро, без миграций;
  • прод (USE_ALEMBIC=1): alembic upgrade head — версионируемые миграции.
Переключение — настройкой, бизнес-логику не трогает.
"""
from __future__ import annotations

import asyncio
import datetime as dt
from collections.abc import AsyncIterator

from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.orm import DeclarativeBase

from app.config import ROOT, settings

engine = create_async_engine(settings.database_url, echo=False, future=True)
SessionLocal = async_sessionmaker(engine, expire_on_commit=False, class_=AsyncSession)


class Base(DeclarativeBase):
    pass


def utcnow() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _alembic_upgrade_head() -> None:
    """Прогнать миграции до head (синхронно — Alembic не async)."""
    from alembic import command
    from alembic.config import Config

    cfg = Config(str(ROOT / "alembic.ini"))
    cfg.set_main_option("script_location", str(ROOT / "alembic"))  # не зависеть от cwd
    command.upgrade(cfg, "head")


# dev-доводка схемы: create_all НЕ добавляет колонки в уже существующие таблицы.
# Чтобы старый normacad.db не падал после добавления полей, идемпотентно дотягиваем
# недостающие колонки (только SQLite, только dev). Прод — Alembic (USE_ALEMBIC=1).
_DEV_COLUMNS: dict[str, dict[str, str]] = {
    "users": {"org_type": "VARCHAR(16) NOT NULL DEFAULT 'individual'"},
    "chat_messages": {"session_id": "INTEGER"},  # мультичаты; FK не нужен (SQLite-дев)
}


async def _ensure_dev_columns(conn) -> None:
    for table, columns in _DEV_COLUMNS.items():
        existing = {
            row[1] for row in await conn.exec_driver_sql(f"PRAGMA table_info({table})")
        }
        for name, ddl in columns.items():
            if name not in existing:
                await conn.exec_driver_sql(f"ALTER TABLE {table} ADD COLUMN {name} {ddl}")


async def init_db() -> None:
    # импорт моделей нужен, чтобы они зарегистрировались в метаданных
    from app import models  # noqa: F401

    if settings.use_alembic:
        await asyncio.to_thread(_alembic_upgrade_head)
        return
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
        if conn.dialect.name == "sqlite":
            await _ensure_dev_columns(conn)


async def get_db() -> AsyncIterator[AsyncSession]:
    async with SessionLocal() as session:
        yield session
