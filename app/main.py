"""Точка входа FastAPI.

Запуск (dev):
    .\\.venv\\Scripts\\python.exe -m uvicorn app.main:app --reload --port 8000
Затем открыть http://127.0.0.1:8000
"""
from __future__ import annotations

from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.staticfiles import StaticFiles

from app.config import ROOT
from app.db import init_db
from app.routers import web
from app.services.csrf import CSRF_COOKIE, issue_token, set_csrf_cookie


@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_db()
    yield


app = FastAPI(title="NormaCAD AI", lifespan=lifespan, docs_url="/api/docs", redoc_url=None)
app.mount("/static", StaticFiles(directory=str(ROOT / "app" / "static")), name="static")


@app.middleware("http")
async def csrf_cookie(request: Request, call_next):
    """Гарантировать наличие CSRF-cookie и положить токен в request.state для форм.

    request.state хранится в scope ASGI и виден роуту/шаблону того же запроса.
    Cookie ставим только когда её ещё нет — значение стабильно между запросами.
    """
    token = request.cookies.get(CSRF_COOKIE)
    is_new = token is None
    if is_new:
        token = issue_token()
    request.state.csrf_token = token
    response = await call_next(request)
    if is_new:
        set_csrf_cookie(response, token)
    return response


app.include_router(web.router)


@app.get("/api/health")
async def health():
    return {"status": "ok"}
