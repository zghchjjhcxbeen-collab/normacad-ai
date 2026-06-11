"""ORM-модели (PLAN.md, раздел 6). На Этапе 0 — на SQLite.

User 1─N Drawing 1─N CheckJob 1─1 Report
                              └─N Finding
AuthSession — серверные сессии (cookie ncad_sid), без JWT.
"""
from __future__ import annotations

import datetime as dt

from sqlalchemy import JSON, DateTime, Float, ForeignKey, Integer, String, Text
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base, utcnow


class User(Base):
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(primary_key=True)
    email: Mapped[str] = mapped_column(String(320), unique=True, index=True)
    password_hash: Mapped[str] = mapped_column(String(255))
    # Тип пользователя задаёт бэкенд ИИ-чата (приватность, решение владельца):
    # individual — допустимо облако Claude; bureau/enterprise — ТОЛЬКО локально/выкл.
    org_type: Mapped[str] = mapped_column(String(16), default="individual")
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class AuthSession(Base):
    __tablename__ = "auth_sessions"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)  # opaque token
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), index=True)
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    expires_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True))


class Drawing(Base):
    __tablename__ = "drawings"

    id: Mapped[int] = mapped_column(primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), index=True)
    original_name: Mapped[str] = mapped_column(String(255))
    stored_path: Mapped[str] = mapped_column(String(1024))
    mime: Mapped[str] = mapped_column(String(128), default="application/dxf")
    size: Mapped[int] = mapped_column(Integer, default=0)
    sha256: Mapped[str] = mapped_column(String(64), index=True)
    format_class: Mapped[str] = mapped_column(String(32), default="vector_dxf")
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class CheckJob(Base):
    __tablename__ = "check_jobs"

    id: Mapped[int] = mapped_column(primary_key=True)
    drawing_id: Mapped[int] = mapped_column(ForeignKey("drawings.id"), index=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), index=True)
    status: Mapped[str] = mapped_column(String(16), default="queued")  # queued|running|done|failed
    error_text: Mapped[str | None] = mapped_column(Text, nullable=True)
    ruleset_version: Mapped[str | None] = mapped_column(String(64), nullable=True)
    units_assumed: Mapped[bool] = mapped_column(default=False)
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    started_at: Mapped[dt.datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    finished_at: Mapped[dt.datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class Finding(Base):
    __tablename__ = "findings"

    id: Mapped[int] = mapped_column(primary_key=True)
    job_id: Mapped[int] = mapped_column(ForeignKey("check_jobs.id"), index=True)
    rule_code: Mapped[str] = mapped_column(String(64))
    rule_clause: Mapped[str] = mapped_column(String(64), default="")
    rule_title: Mapped[str] = mapped_column(String(255), default="")
    severity: Mapped[str] = mapped_column(String(16))
    message: Mapped[str] = mapped_column(Text)
    fix: Mapped[str] = mapped_column(Text, default="")
    page: Mapped[int] = mapped_column(Integer, default=1)
    bbox: Mapped[list | None] = mapped_column(JSON, nullable=True)  # [x,y,w,h] в мм
    handle: Mapped[str | None] = mapped_column(String(32), nullable=True)
    source: Mapped[str] = mapped_column(String(16), default="deterministic")
    confidence: Mapped[float | None] = mapped_column(Float, nullable=True)
    status: Mapped[str] = mapped_column(String(16), default="open")  # open|accepted|dismissed


class Report(Base):
    __tablename__ = "reports"

    id: Mapped[int] = mapped_column(primary_key=True)
    job_id: Mapped[int] = mapped_column(ForeignKey("check_jobs.id"), unique=True, index=True)
    summary: Mapped[str | None] = mapped_column(Text, nullable=True)
    total_findings: Mapped[int] = mapped_column(Integer, default=0)
    counts_by_severity: Mapped[dict] = mapped_column(JSON, default=dict)
    ruleset_version: Mapped[str | None] = mapped_column(String(64), nullable=True)
    generated_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class ChatSession(Base):
    """Отдельный разговор ИИ-чата (мультичаты: «Новый чат», история, поиск)."""

    __tablename__ = "chat_sessions"

    id: Mapped[int] = mapped_column(primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), index=True)
    title: Mapped[str] = mapped_column(String(200), default="Новый чат")
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    updated_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class ChatMessage(Base):
    """Сообщение ИИ-чата (вопросы клиента + ответы со справочником по ГОСТ)."""

    __tablename__ = "chat_messages"

    id: Mapped[int] = mapped_column(primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id"), index=True)
    session_id: Mapped[int | None] = mapped_column(
        ForeignKey("chat_sessions.id"), index=True, nullable=True
    )
    role: Mapped[str] = mapped_column(String(16))  # user | assistant
    content: Mapped[str] = mapped_column(Text)
    citations: Mapped[list | None] = mapped_column(JSON, nullable=True)  # [{code,title}]
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
