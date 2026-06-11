"""Тесты безопасности (Этап 1): CSRF и rate-limit.

Веб-тесты бьют по путям, которые НЕ пишут в БД (логин неизвестного юзера,
POST без токена отклоняется до тела), чтобы не зависеть от состояния normacad.db
и не плодить записи. Happy-path многочастной загрузки с CSRF покрыт scripts/smoke_web.py.
"""
from __future__ import annotations

import pathlib
import sys

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.services.ratelimit import RateLimiter  # noqa: E402


# ---------- RateLimiter (чистая логика, без БД) ----------

def test_ratelimiter_blocks_after_limit():
    rl = RateLimiter(max_hits=3, window_seconds=60)
    assert rl.allow("k") is True   # 1
    assert rl.allow("k") is True   # 2
    assert rl.allow("k") is True   # 3
    assert rl.allow("k") is False  # 4 — превышен
    assert rl.allow("other") is True  # другой ключ не затронут


def test_ratelimiter_reset():
    rl = RateLimiter(max_hits=1, window_seconds=60)
    assert rl.allow("k") is True
    assert rl.allow("k") is False
    rl.reset("k")
    assert rl.allow("k") is True  # после сброса снова можно


# ---------- CSRF и login-лимит через TestClient ----------

@pytest.fixture(scope="module")
def client():
    from fastapi.testclient import TestClient

    from app.main import app
    with TestClient(app) as c:
        yield c


def _csrf(client) -> str:
    client.get("/login")
    token = client.cookies.get("ncad_csrf")
    assert token, "ожидалась CSRF-cookie после GET /login"
    return token


def test_csrf_missing_field_rejected(client):
    _csrf(client)  # cookie есть, но поле формы не шлём
    r = client.post("/login", data={"email": "a@b.c", "password": "x"}, follow_redirects=False)
    assert r.status_code == 403, r.status_code


def test_csrf_wrong_field_rejected(client):
    _csrf(client)
    r = client.post("/login", data={"email": "a@b.c", "password": "x", "csrf_token": "wrong"},
                    follow_redirects=False)
    assert r.status_code == 403, r.status_code


def test_csrf_valid_token_passes(client):
    token = _csrf(client)
    # неизвестный пользователь -> 401 (не 403): CSRF пройден, до записи в БД не доходит
    r = client.post("/login", data={"email": "nobody-unique@x.z", "password": "whatever",
                                    "csrf_token": token}, follow_redirects=False)
    assert r.status_code in (401, 429), r.status_code
    assert r.status_code != 403


def test_login_rate_limit(client):
    token = _csrf(client)
    email = "ratelimit-probe@x.z"  # отдельный ключ, не пересекается с другими тестами
    statuses = []
    for _ in range(11):
        r = client.post("/login", data={"email": email, "password": "bad", "csrf_token": token},
                        follow_redirects=False)
        statuses.append(r.status_code)
    assert statuses[0] == 401, statuses
    assert 429 in statuses, statuses           # лимит сработал
    assert statuses[-1] == 429, statuses       # 11-я попытка отклонена
