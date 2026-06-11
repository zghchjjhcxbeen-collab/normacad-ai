"""Хеширование паролей — прямой bcrypt (урок из handoff: passlib не дружит с bcrypt 4/5).

bcrypt не принимает строки длиннее 72 байт — явно усекаем.
"""
from __future__ import annotations

import bcrypt

_EMAIL_OK = "@"


def hash_password(password: str) -> str:
    pw = password.encode("utf-8")[:72]
    return bcrypt.hashpw(pw, bcrypt.gensalt()).decode("utf-8")


def verify_password(password: str, password_hash: str) -> bool:
    try:
        return bcrypt.checkpw(password.encode("utf-8")[:72], password_hash.encode("utf-8"))
    except (ValueError, TypeError):
        return False


def looks_like_email(email: str) -> bool:
    """Минимальная проверка без внешних зависимостей (как в handoff — без EmailStr)."""
    email = email.strip()
    if _EMAIL_OK not in email or email.startswith("@") or email.endswith("@"):
        return False
    local, _, domain = email.partition("@")
    return bool(local) and "." in domain and not domain.endswith(".")
