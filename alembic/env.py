"""Alembic env: схема из app.models, URL из настроек приложения.

Миграции выполняются СИНХРОННЫМ драйвером (sqlite/postgresql), хотя приложение
работает через async (aiosqlite/asyncpg). Это стандартный приём: Alembic не
нуждается в async-движке. URL читается живьём из env DATABASE_URL (с откатом на
settings), поэтому autogenerate и тесты могут указать временную БД.
"""
from __future__ import annotations

import os
import sys
from logging.config import fileConfig
from pathlib import Path

from alembic import context
from sqlalchemy import engine_from_config, pool

# корень проекта в sys.path, чтобы импортировались app.*
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.config import settings  # noqa: E402
from app.db import Base  # noqa: E402
from app import models  # noqa: F401,E402  — регистрация таблиц в Base.metadata

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def _sync_url() -> str:
    """async-URL приложения → синхронный для Alembic (aiosqlite/asyncpg убираем)."""
    url = os.environ.get("DATABASE_URL", settings.database_url)
    return url.replace("+aiosqlite", "").replace("+asyncpg", "")


def run_migrations_offline() -> None:
    context.configure(
        url=_sync_url(),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        render_as_batch=True,
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    section = config.get_section(config.config_ini_section) or {}
    section["sqlalchemy.url"] = _sync_url()
    connectable = engine_from_config(section, prefix="sqlalchemy.", poolclass=pool.NullPool)
    with connectable.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            render_as_batch=True,  # SQLite не умеет ALTER COLUMN — Alembic делает через batch
        )
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
