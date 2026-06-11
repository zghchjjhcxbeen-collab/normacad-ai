"""SSR-роуты: аутентификация, загрузка DXF, статус задачи, отчёт.

CSRF и rate-limit — Этап 1 (PLAN.md). Пока cookie SameSite=Lax.
"""
from __future__ import annotations

import asyncio
import hashlib
import pathlib

from fastapi import APIRouter, BackgroundTasks, Depends, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates
from sqlalchemy import func, or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app import gostdb
from app.chat import answer as chat_answer
from app.config import ROOT, settings
from app.db import get_db, utcnow
from app.jobs import enqueue
from app.models import ChatMessage, ChatSession, CheckJob, Drawing, Finding, Report, User
from app.parsing.dispatch import parse_drawing
from app.ruleset import RULE_META
from app.services.csrf import verify_csrf
from app.services.ratelimit import login_limiter, upload_limiter
from app.services.security import hash_password, looks_like_email, verify_password
from app.services.uploads import content_matches_ext, sanitize_filename, unsupported_ext_message
from app.services.sessions import (
    create_session,
    delete_session,
    get_user_by_email,
    get_user_by_token,
)
from app.svgview import render_drawing_svg

router = APIRouter()
templates = Jinja2Templates(directory=str(ROOT / "app" / "templates"))

_SEV_RANK = {"critical": 0, "major": 1, "minor": 2, "info": 3}


def _build_groundings(rule_codes: set[str]) -> dict[str, dict]:
    """Для каждого правила в отчёте подтянуть РЕАЛЬНУЮ формулировку пункта из базы ГОСТ.

    Текст берётся буквально из базы (анти-галлюцинация); если пункт не нашёлся — кода
    нет в словаре, и в UI показывается только ссылка на стандарт. Числа отсюда не идут.
    """
    out: dict[str, dict] = {}
    for meta in RULE_META.values():
        code = meta["code"]
        if code in rule_codes and code not in out:
            quote = gostdb.grounding_quote(code, meta.get("ground_anchors", ()))
            if quote:
                out[code] = quote
    return out


async def get_current_user(request: Request, db: AsyncSession = Depends(get_db)) -> User | None:
    token = request.cookies.get(settings.session_cookie)
    return await get_user_by_token(db, token)


def _set_session_cookie(response, token: str) -> None:
    response.set_cookie(
        settings.session_cookie, token,
        httponly=True, samesite="lax", secure=settings.cookie_secure,  # secure в проде (COOKIE_SECURE=1)
        max_age=settings.session_ttl_days * 86400,
    )


# ---------- аутентификация ----------

@router.get("/")
async def index(user: User | None = Depends(get_current_user)):
    return RedirectResponse("/dashboard" if user else "/login", status_code=303)


@router.get("/login")
async def login_page(request: Request, user: User | None = Depends(get_current_user)):
    if user:
        return RedirectResponse("/dashboard", status_code=303)
    return templates.TemplateResponse(request, "login.html", {"error": None})


@router.post("/login", dependencies=[Depends(verify_csrf)])
async def login_action(
    request: Request,
    email: str = Form(...),
    password: str = Form(...),
    db: AsyncSession = Depends(get_db),
):
    ip = request.client.host if request.client else "unknown"
    rl_key = f"{ip}:{email.strip().lower()}"
    if not login_limiter.allow(rl_key):
        return templates.TemplateResponse(
            request, "login.html",
            {"error": "Слишком много попыток входа. Подождите несколько минут."},
            status_code=429,
        )
    user = await get_user_by_email(db, email)
    if user is None or not verify_password(password, user.password_hash):
        return templates.TemplateResponse(
            request, "login.html", {"error": "Неверный email или пароль."}, status_code=401
        )
    login_limiter.reset(rl_key)  # успех — не наказывать легитимного пользователя
    token = await create_session(db, user.id)
    resp = RedirectResponse("/dashboard", status_code=303)
    _set_session_cookie(resp, token)
    return resp


@router.get("/register")
async def register_page(request: Request, user: User | None = Depends(get_current_user)):
    if user:
        return RedirectResponse("/dashboard", status_code=303)
    return templates.TemplateResponse(request, "register.html", {"error": None})


ORG_TYPES = {"individual", "bureau", "enterprise"}


@router.post("/register", dependencies=[Depends(verify_csrf)])
async def register_action(
    request: Request,
    email: str = Form(...),
    password: str = Form(...),
    org_type: str = Form("individual"),
    db: AsyncSession = Depends(get_db),
):
    email = email.strip().lower()
    error = None
    if not looks_like_email(email):
        error = "Введите корректный email."
    elif len(password) < 8:
        error = "Пароль должен быть не короче 8 символов."
    elif await get_user_by_email(db, email):
        error = "Пользователь с таким email уже существует."
    if error:
        return templates.TemplateResponse(
            request, "register.html", {"error": error}, status_code=400
        )
    # Тип определяет приватность чата: bureau/enterprise никогда не уходят в облако.
    org_type = org_type if org_type in ORG_TYPES else "individual"
    user = User(email=email, password_hash=hash_password(password), org_type=org_type)
    db.add(user)
    await db.commit()
    await db.refresh(user)
    token = await create_session(db, user.id)
    resp = RedirectResponse("/dashboard", status_code=303)
    _set_session_cookie(resp, token)
    return resp


@router.post("/logout", dependencies=[Depends(verify_csrf)])
async def logout_action(request: Request, db: AsyncSession = Depends(get_db)):
    await delete_session(db, request.cookies.get(settings.session_cookie))
    resp = RedirectResponse("/login", status_code=303)
    resp.delete_cookie(settings.session_cookie)
    return resp


# ---------- дашборд и загрузка ----------

@router.get("/dashboard")
async def dashboard(
    request: Request,
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    rows = (await db.execute(
        select(CheckJob, Drawing)
        .join(Drawing, CheckJob.drawing_id == Drawing.id)
        .where(CheckJob.user_id == user.id)
        .order_by(CheckJob.id.desc())
        .limit(50)
    )).all()
    return templates.TemplateResponse(
        request, "dashboard.html", {"user": user, "rows": rows}
    )


@router.get("/check")
async def check_page(request: Request, user: User | None = Depends(get_current_user)):
    if not user:
        return RedirectResponse("/login", status_code=303)
    return templates.TemplateResponse(request, "check.html", {"user": user})


def _upload_error(request: Request, user: User, message: str, status_code: int):
    """Ошибку загрузки показываем на странице проверки (там же drag-drop)."""
    return templates.TemplateResponse(
        request, "check.html", {"user": user, "error": message}, status_code=status_code
    )


@router.post("/uploads", dependencies=[Depends(verify_csrf)])
async def upload_action(
    request: Request,
    background_tasks: BackgroundTasks,
    file: UploadFile = File(...),
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)

    if not upload_limiter.allow(str(user.id)):
        return _upload_error(request, user,
            "Слишком много загрузок подряд. Подождите несколько минут.", 429)

    name = sanitize_filename(file.filename or "drawing.dxf")
    ext = pathlib.Path(name).suffix.lower()
    if ext not in settings.allowed_upload_ext:
        return _upload_error(request, user, unsupported_ext_message(ext), 400)

    content = await file.read()
    if len(content) > settings.max_upload_bytes:
        return _upload_error(request, user,
            f"Файл больше лимита {settings.max_upload_mb} МБ.", 400)

    if not content_matches_ext(content, ext):
        return _upload_error(request, user,
            f"Содержимое файла не похоже на {ext} (проверка по сигнатуре). "
            "Загрузите настоящий DXF или вектор-PDF.", 400)

    sha = hashlib.sha256(content).hexdigest()
    user_dir = settings.storage_dir / str(user.id)
    user_dir.mkdir(parents=True, exist_ok=True)
    stored = user_dir / f"{sha}{ext}"
    if not stored.exists():
        stored.write_bytes(content)

    is_pdf = ext == ".pdf"
    drawing = Drawing(
        user_id=user.id, original_name=name, stored_path=str(stored),
        mime="application/pdf" if is_pdf else "application/dxf",
        size=len(content), sha256=sha,
        format_class="vector_pdf" if is_pdf else "vector_dxf",  # воркер уточнит (raster_pdf)
    )
    db.add(drawing)
    await db.commit()
    await db.refresh(drawing)

    job = CheckJob(drawing_id=drawing.id, user_id=user.id, status="queued")
    db.add(job)
    await db.commit()
    await db.refresh(job)

    enqueue(background_tasks, job.id)
    return RedirectResponse(f"/jobs/{job.id}", status_code=303)


# ---------- задача и отчёт ----------

@router.get("/jobs/{job_id}")
async def job_page(
    job_id: int,
    request: Request,
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    job = await db.get(CheckJob, job_id)
    if job is None or job.user_id != user.id:
        return RedirectResponse("/dashboard", status_code=303)
    drawing = await db.get(Drawing, job.drawing_id)
    return templates.TemplateResponse(
        request, "job.html", {"user": user, "job": job, "drawing": drawing}
    )


@router.get("/jobs/{job_id}/fragment")
async def job_fragment(
    job_id: int,
    request: Request,
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    job = await db.get(CheckJob, job_id)
    if job is None or job.user_id != user.id:
        return RedirectResponse("/dashboard", status_code=303)

    ctx: dict = {"job": job}
    if job.status == "done":
        report = (await db.execute(select(Report).where(Report.job_id == job.id))).scalar_one_or_none()
        all_findings = (await db.execute(select(Finding).where(Finding.job_id == job.id))).scalars().all()
        all_findings = sorted(all_findings, key=lambda f: _SEV_RANK.get(f.severity, 9))
        # Замечания ИИ и добавленные нормоконтролёром вручную (пропущенные ИИ) — разные секции.
        findings = [f for f in all_findings if f.source != "manual"]
        manual_findings = [f for f in all_findings if f.source == "manual"]
        drawing = await db.get(Drawing, job.drawing_id)
        parsed = await asyncio.to_thread(parse_drawing, drawing.stored_path)
        groundings = await asyncio.to_thread(
            _build_groundings, {f.rule_code for f in findings}
        )
        # Реальный рендер чертежа (ezdxf) ~0,7 с — в отдельном потоке, не блокируем цикл.
        # Подсвечиваем только замечания ИИ (у ручных нет локатора — рендер их и так пропустит).
        svg = await asyncio.to_thread(render_drawing_svg, drawing.stored_path, parsed, findings)
        ctx.update({
            "report": report,
            "findings": findings,
            "manual_findings": manual_findings,
            "groundings": groundings,
            "svg": svg,
            "warnings": parsed.warnings,
        })
    return templates.TemplateResponse(request, "_job_fragment.html", ctx)


# ---------- обратная связь нормоконтролёра (триаж + пропущенные замечания) ----------
#
# Единственный источник истины о точности до появления реального корпуса (PLAN.md, §1a/§9):
#   • триаж замечания ИИ: accepted (подтверждено) / dismissed (ложное) — сигнал false-positive;
#   • ручное замечание (source="manual") — то, что ИИ ПРОПУСТИЛ — сигнал false-negative.
# Роуты htmx-фрагментные: возвращают перерисованный <li> замечания (или пусто при удалении).

_FINDING_STATUSES = {"open", "accepted", "dismissed"}
_SEVERITIES = {"critical", "major", "minor", "info"}


async def _owned_finding(db: AsyncSession, user_id: int, finding_id: int) -> Finding | None:
    """Замечание с проверкой владения (через job.user_id). Чужое/несуществующее → None."""
    f = await db.get(Finding, finding_id)
    if f is None:
        return None
    job = await db.get(CheckJob, f.job_id)
    return f if job is not None and job.user_id == user_id else None


def _one_grounding(rule_code: str) -> dict | None:
    """Цитата ГОСТ для одного правила (чтобы перерисованный <li> был идентичен исходному)."""
    return _build_groundings({rule_code}).get(rule_code)


@router.post("/findings/{finding_id}/status", dependencies=[Depends(verify_csrf)])
async def finding_set_status(
    finding_id: int,
    request: Request,
    status: str = Form(...),
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    """Триаж замечания ИИ нормоконтролёром: open|accepted|dismissed."""
    if not user:
        raise HTTPException(status_code=401)
    if status not in _FINDING_STATUSES:
        raise HTTPException(status_code=400, detail="Недопустимый статус.")
    f = await _owned_finding(db, user.id, finding_id)
    if f is None:
        raise HTTPException(status_code=404)
    f.status = status
    await db.commit()
    g = await asyncio.to_thread(_one_grounding, f.rule_code)
    return templates.TemplateResponse(request, "_finding.html", {"f": f, "g": g})


@router.post("/reports/{report_id}/manual-finding", dependencies=[Depends(verify_csrf)])
async def add_manual_finding(
    report_id: int,
    request: Request,
    severity: str = Form("major"),
    message: str = Form(...),
    rule_code: str = Form(""),
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    """Добавить замечание, которое ИИ пропустил (источник метрики false-negative)."""
    if not user:
        raise HTTPException(status_code=401)
    report = await db.get(Report, report_id)
    if report is None:
        raise HTTPException(status_code=404)
    job = await db.get(CheckJob, report.job_id)
    if job is None or job.user_id != user.id:
        raise HTTPException(status_code=404)
    message = (message or "").strip()
    if not message:
        raise HTTPException(status_code=400, detail="Опишите замечание.")
    severity = severity if severity in _SEVERITIES else "major"
    f = Finding(
        job_id=job.id,
        rule_code=(rule_code or "").strip()[:64],
        rule_clause="",
        rule_title="Замечание нормоконтролёра",
        severity=severity,
        message=message,
        fix="",
        source="manual",
        status="open",
    )
    db.add(f)
    await db.commit()
    await db.refresh(f)
    return templates.TemplateResponse(request, "_finding.html", {"f": f, "g": None})


@router.post("/findings/{finding_id}/delete", dependencies=[Depends(verify_csrf)])
async def delete_manual_finding(
    finding_id: int,
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    """Удалить ручное замечание (ошибочно добавленное). Замечания ИИ не удаляются — для них триаж."""
    if not user:
        raise HTTPException(status_code=401)
    f = await _owned_finding(db, user.id, finding_id)
    if f is None:
        raise HTTPException(status_code=404)
    if f.source != "manual":
        raise HTTPException(status_code=403, detail="Удалять можно только ручные замечания.")
    await db.delete(f)
    await db.commit()
    return HTMLResponse("")  # пустое тело + hx-swap=outerHTML → htmx убирает <li>


# ---------- ИИ-чат (мультичаты) ----------

async def _owned_session(db: AsyncSession, user_id: int, session_id: int) -> ChatSession | None:
    """Сессия чата с проверкой владения (чужую/несуществующую не отдаём)."""
    s = await db.get(ChatSession, session_id)
    return s if s is not None and s.user_id == user_id else None


async def _latest_session(db: AsyncSession, user_id: int) -> ChatSession | None:
    return (await db.execute(
        select(ChatSession).where(ChatSession.user_id == user_id)
        .order_by(ChatSession.id.desc()).limit(1)
    )).scalar_one_or_none()


async def _session_messages(db: AsyncSession, session_id: int):
    return (await db.execute(
        select(ChatMessage).where(ChatMessage.session_id == session_id)
        .order_by(ChatMessage.id).limit(200)
    )).scalars().all()


async def _new_or_reuse_empty(db: AsyncSession, user_id: int) -> ChatSession:
    """Создать новую сессию — но если последняя ещё пустая, переиспользовать её (без спама)."""
    latest = await _latest_session(db, user_id)
    if latest is not None:
        cnt = (await db.execute(
            select(func.count(ChatMessage.id)).where(ChatMessage.session_id == latest.id)
        )).scalar_one()
        if cnt == 0:
            return latest
    s = ChatSession(user_id=user_id)
    db.add(s)
    await db.commit()
    await db.refresh(s)
    return s


@router.get("/chat")
async def chat_redirect(
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    s = await _latest_session(db, user.id) or await _new_or_reuse_empty(db, user.id)
    return RedirectResponse(f"/chat/{s.id}", status_code=303)


@router.get("/chat/new")
async def chat_new(
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    s = await _new_or_reuse_empty(db, user.id)
    return RedirectResponse(f"/chat/{s.id}", status_code=303)


@router.get("/chat/{session_id}")
async def chat_session_page(
    session_id: int,
    request: Request,
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    s = await _owned_session(db, user.id, session_id)
    if s is None:
        return RedirectResponse("/chat", status_code=303)
    msgs = await _session_messages(db, s.id)
    return templates.TemplateResponse(
        request, "chat.html", {"user": user, "session": s, "messages": msgs}
    )


@router.post("/chat/{session_id}", dependencies=[Depends(verify_csrf)])
async def chat_send(
    session_id: int,
    request: Request,
    question: str = Form(...),
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    if not user:
        return RedirectResponse("/login", status_code=303)
    s = await _owned_session(db, user.id, session_id)
    if s is None:
        return RedirectResponse("/chat", status_code=303)
    question = question.strip()
    if question:
        db.add(ChatMessage(user_id=user.id, session_id=s.id, role="user", content=question))
        if not s.title or s.title == "Новый чат":  # заголовок — из первого вопроса
            s.title = (question[:60] + "…") if len(question) > 60 else question
        s.updated_at = utcnow()
        await db.commit()
        hist = (await db.execute(
            select(ChatMessage).where(ChatMessage.session_id == s.id)
            .order_by(ChatMessage.id).limit(20)
        )).scalars().all()
        history = [{"role": h.role, "content": h.content} for h in hist]
        result = await asyncio.to_thread(chat_answer, question, history, user.org_type)
        db.add(ChatMessage(
            user_id=user.id, session_id=s.id, role="assistant",
            content=result["answer"], citations=result.get("citations"),
        ))
        s.updated_at = utcnow()
        await db.commit()
    msgs = await _session_messages(db, s.id)
    return templates.TemplateResponse(request, "_chat_messages.html", {"messages": msgs})


@router.get("/chats")
async def chat_history(
    request: Request,
    q: str = "",
    user: User | None = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
):
    """История чатов: список разговоров с поиском по заголовку/содержимому (?q=)."""
    if not user:
        return RedirectResponse("/login", status_code=303)
    q = (q or "").strip()
    stmt = select(ChatSession).where(ChatSession.user_id == user.id)
    if q:
        like = f"%{q}%"
        in_msgs = select(ChatMessage.session_id).where(
            ChatMessage.user_id == user.id, ChatMessage.content.ilike(like)
        )
        stmt = stmt.where(or_(ChatSession.title.ilike(like), ChatSession.id.in_(in_msgs)))
    sessions = (await db.execute(
        stmt.order_by(ChatSession.updated_at.desc()).limit(100)
    )).scalars().all()
    return templates.TemplateResponse(
        request, "chat_history.html", {"user": user, "sessions": sessions, "q": q}
    )


@router.get("/gost/{num}")
async def gost_pdf(num: str):
    """Открыть PDF цитируемого ГОСТа из базы (для проверки человеком)."""
    safe = num.replace("..", "").replace("/", "").replace("\\", "")
    pdf = settings.gost_db_dir / "pdf" / "eskd" / f"{safe}.pdf"
    if not pdf.exists():
        return RedirectResponse("/dashboard", status_code=303)
    return FileResponse(str(pdf), media_type="application/pdf", filename=f"ГОСТ {safe}.pdf")
