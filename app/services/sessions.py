"""Серверные сессии в БД (cookie ncad_sid хранит только opaque-токен)."""
from __future__ import annotations

import datetime as dt
import secrets

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import settings
from app.db import utcnow
from app.models import AuthSession, User


async def create_session(db: AsyncSession, user_id: int) -> str:
    token = secrets.token_urlsafe(32)
    db.add(AuthSession(
        id=token,
        user_id=user_id,
        expires_at=utcnow() + dt.timedelta(days=settings.session_ttl_days),
    ))
    await db.commit()
    return token


async def get_user_by_token(db: AsyncSession, token: str | None) -> User | None:
    if not token:
        return None
    sess = await db.get(AuthSession, token)
    if sess is None:
        return None
    expires = sess.expires_at
    if expires.tzinfo is None:  # SQLite может вернуть naive datetime
        expires = expires.replace(tzinfo=dt.timezone.utc)
    if expires < utcnow():
        await db.execute(delete(AuthSession).where(AuthSession.id == token))
        await db.commit()
        return None
    return await db.get(User, sess.user_id)


async def delete_session(db: AsyncSession, token: str | None) -> None:
    if not token:
        return
    await db.execute(delete(AuthSession).where(AuthSession.id == token))
    await db.commit()


async def get_user_by_email(db: AsyncSession, email: str) -> User | None:
    res = await db.execute(select(User).where(User.email == email.strip().lower()))
    return res.scalar_one_or_none()
