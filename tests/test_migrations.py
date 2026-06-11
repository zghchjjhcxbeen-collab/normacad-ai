"""Тест миграций Alembic (Этап 1): upgrade head на чистой БД создаёт всю схему."""
from __future__ import annotations

import os
import pathlib
import sqlite3
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def test_alembic_upgrade_creates_schema(tmp_path):
    from alembic import command
    from alembic.config import Config

    db = tmp_path / "m.db"
    old = os.environ.get("DATABASE_URL")
    os.environ["DATABASE_URL"] = f"sqlite+aiosqlite:///{db.as_posix()}"
    try:
        cfg = Config(str(ROOT / "alembic.ini"))
        cfg.set_main_option("script_location", str(ROOT / "alembic"))
        command.upgrade(cfg, "head")

        con = sqlite3.connect(db)
        names = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        con.close()
    finally:
        if old is None:
            os.environ.pop("DATABASE_URL", None)
        else:
            os.environ["DATABASE_URL"] = old

    for table in ("users", "auth_sessions", "drawings", "check_jobs",
                  "findings", "reports", "chat_messages", "alembic_version"):
        assert table in names, f"таблица {table} не создана миграцией"
