"""Тесты обратной связи нормоконтролёра (пункт D): триаж замечаний ИИ и
добавление пропущенных ИИ замечаний.

Эти роуты ПИШУТ в БД, поэтому идут на ВРЕМЕННОЙ SQLite (грабли №10: не трогать
реальный normacad.db). Изоляция — через FastAPI dependency_overrides:
  • get_db        → сессия на временной БД;
  • get_current_user → подставной пользователь (только .id используется роутами).
Сидинг и проверки БД — отдельным async-движком на том же файле (свой event loop),
чтобы не пересекать циклы с TestClient. Утверждения — по HTTP-ответам и по БД.
"""
from __future__ import annotations

import asyncio
import pathlib
import sys

import pytest
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.db import Base, get_db  # noqa: E402
from app.main import app  # noqa: E402
from app.models import CheckJob, Drawing, Finding, Report, User  # noqa: E402
from app.routers.web import get_current_user  # noqa: E402


async def _seed(url: str) -> dict:
    """Создать схему и наполнить временную БД. Возвращает id-шники (plain int)."""
    eng = create_async_engine(url, poolclass=NullPool)
    async with eng.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    async with async_sessionmaker(eng, expire_on_commit=False)() as s:
        user = User(email="nk@test.local", password_hash="x", org_type="enterprise")
        other = User(email="other@test.local", password_hash="x", org_type="individual")
        s.add_all([user, other])
        await s.commit()
        await s.refresh(user)
        await s.refresh(other)
        drawing = Drawing(user_id=user.id, original_name="d.dxf",
                          stored_path="dummy.dxf", sha256="abc", format_class="vector_dxf")
        s.add(drawing)
        await s.commit()
        await s.refresh(drawing)
        job = CheckJob(drawing_id=drawing.id, user_id=user.id, status="done")
        s.add(job)
        await s.commit()
        await s.refresh(job)
        report = Report(job_id=job.id, total_findings=1, counts_by_severity={"major": 1},
                        ruleset_version="test")
        finding = Finding(job_id=job.id, rule_code="ГОСТ 2.301-68", rule_clause="п. 1",
                          rule_title="Формат", severity="major", message="Формат не из ряда",
                          source="deterministic", status="open")
        s.add_all([report, finding])
        await s.commit()
        await s.refresh(report)
        await s.refresh(finding)
        ids = {"user_id": user.id, "other_id": other.id, "report_id": report.id,
               "finding_id": finding.id, "job_id": job.id}
    await eng.dispose()
    return ids


async def _query(url: str, fn):
    eng = create_async_engine(url, poolclass=NullPool)
    async with async_sessionmaker(eng, expire_on_commit=False)() as s:
        out = await fn(s)
    await eng.dispose()
    return out


@pytest.fixture()
def ctx(tmp_path):
    """Временная БД + переопределённые зависимости + TestClient + CSRF-токен."""
    from fastapi.testclient import TestClient

    url = f"sqlite+aiosqlite:///{(tmp_path / 'fb.db').as_posix()}"
    ids = asyncio.run(_seed(url))

    override_engine = create_async_engine(url, poolclass=NullPool)
    OverrideSession = async_sessionmaker(override_engine, expire_on_commit=False)
    current = {"user_id": ids["user_id"]}  # мутабельно — чтобы менять «кто вошёл» в тесте

    async def override_get_db():
        async with OverrideSession() as s:
            yield s

    async def override_user():
        uid = current["user_id"]
        return User(id=uid) if uid is not None else None

    app.dependency_overrides[get_db] = override_get_db
    app.dependency_overrides[get_current_user] = override_user
    try:
        with TestClient(app) as client:
            client.get("/api/health")  # middleware ставит CSRF-cookie
            token = client.cookies.get("ncad_csrf")
            assert token
            yield {"client": client, "ids": ids, "url": url, "csrf": token, "current": current}
    finally:
        app.dependency_overrides.clear()
        asyncio.run(override_engine.dispose())


# ---------- триаж замечания ИИ ----------

def test_dismiss_marks_finding_and_persists(ctx):
    c, ids, url, token = ctx["client"], ctx["ids"], ctx["url"], ctx["csrf"]
    r = c.post(f"/findings/{ids['finding_id']}/status",
               data={"status": "dismissed", "csrf_token": token})
    assert r.status_code == 200, r.text
    assert "is-dismissed" in r.text and "Отмечено ложным" in r.text

    async def read(s):
        return (await s.get(Finding, ids["finding_id"])).status
    assert asyncio.run(_query(url, read)) == "dismissed"  # сохранилось в БД


def test_invalid_status_rejected(ctx):
    c, ids, token = ctx["client"], ctx["ids"], ctx["csrf"]
    r = c.post(f"/findings/{ids['finding_id']}/status",
               data={"status": "garbage", "csrf_token": token})
    assert r.status_code == 400, r.text


def test_status_requires_csrf(ctx):
    c, ids = ctx["client"], ctx["ids"]
    r = c.post(f"/findings/{ids['finding_id']}/status", data={"status": "dismissed"})
    assert r.status_code == 403, r.text  # нет токена → CSRF отклоняет до записи


def test_triage_foreign_finding_404(ctx):
    """Чужое замечание триажить нельзя (проверка владения через job.user_id)."""
    c, ids, token, current = ctx["client"], ctx["ids"], ctx["csrf"], ctx["current"]
    current["user_id"] = ids["other_id"]  # вошёл другой пользователь
    r = c.post(f"/findings/{ids['finding_id']}/status",
               data={"status": "accepted", "csrf_token": token})
    assert r.status_code == 404, r.text


# ---------- пропущенное ИИ (ручное) замечание ----------

def test_add_manual_finding_creates_manual_source(ctx):
    c, ids, url, token = ctx["client"], ctx["ids"], ctx["url"], ctx["csrf"]
    r = c.post(f"/reports/{ids['report_id']}/manual-finding",
               data={"severity": "critical", "rule_code": "2.305",
                     "message": "Недостаточно видов", "csrf_token": token})
    assert r.status_code == 200, r.text
    assert "вручную" in r.text and "Недостаточно видов" in r.text

    async def manual(s):
        rows = (await s.execute(
            select(Finding).where(Finding.job_id == ids["job_id"], Finding.source == "manual")
        )).scalars().all()
        return rows
    rows = asyncio.run(_query(url, manual))
    assert len(rows) == 1
    assert rows[0].severity == "critical" and rows[0].message == "Недостаточно видов"


def test_add_manual_finding_empty_message_rejected(ctx):
    c, ids, token = ctx["client"], ctx["ids"], ctx["csrf"]
    r = c.post(f"/reports/{ids['report_id']}/manual-finding",
               data={"severity": "major", "message": "   ", "csrf_token": token})
    assert r.status_code == 400, r.text


def test_delete_manual_finding(ctx):
    c, ids, url, token = ctx["client"], ctx["ids"], ctx["url"], ctx["csrf"]
    # создать ручное
    c.post(f"/reports/{ids['report_id']}/manual-finding",
           data={"severity": "minor", "message": "удалить меня", "csrf_token": token})

    async def manual_id(s):
        return (await s.execute(
            select(Finding.id).where(Finding.source == "manual")
        )).scalar_one()
    mid = asyncio.run(_query(url, manual_id))

    r = c.post(f"/findings/{mid}/delete", data={"csrf_token": token})
    assert r.status_code == 200 and r.text == ""

    async def count_manual(s):
        return (await s.execute(
            select(func.count(Finding.id)).where(Finding.source == "manual")
        )).scalar_one()
    assert asyncio.run(_query(url, count_manual)) == 0  # удалилось


def test_cannot_delete_ai_finding(ctx):
    """Замечание ИИ удалять нельзя — только триаж (dismiss). Удаление → 403."""
    c, ids, token = ctx["client"], ctx["ids"], ctx["csrf"]
    r = c.post(f"/findings/{ids['finding_id']}/delete", data={"csrf_token": token})
    assert r.status_code == 403, r.text
