"""CSRF-защита (Этап 1, безопасность): double-submit cookie.

Схема: на каждом ответе, если у клиента ещё нет CSRF-cookie, middleware его
ставит (HttpOnly, тот же SameSite/secure, что и сессия) и кладёт токен в
``request.state.csrf_token``. Шаблоны рендерят токен скрытым полем ``csrf_token``
в каждую форму (htmx сериализует поле так же, как обычная форма). На небезопасном
методе значение из тела/заголовка сверяется с cookie постоянным сравнением.

Почему double-submit, а не «токен от сессии»: формы входа и регистрации
работают ДО появления серверной сессии — токен от сессии там негде взять.
Cookie-схема защищает и эти формы. Безопасность держится на том, что чужой
origin не может прочитать cookie жертвы (Same-Origin Policy), а значит не
подберёт совпадающее поле.
"""
from __future__ import annotations

import secrets

from fastapi import HTTPException, Request, Response

from app.config import settings

CSRF_COOKIE = "ncad_csrf"
FORM_FIELD = "csrf_token"
HEADER = "X-CSRF-Token"


def issue_token() -> str:
    return secrets.token_urlsafe(32)


def set_csrf_cookie(response: Response, token: str) -> None:
    response.set_cookie(
        CSRF_COOKIE, token,
        httponly=True, samesite="lax", secure=settings.cookie_secure,
        max_age=settings.session_ttl_days * 86400,
    )


async def verify_csrf(request: Request) -> None:
    """Зависимость для небезопасных роутов: сверить токен запроса с cookie.

    Токен берётся из заголовка ``X-CSRF-Token`` (на будущее, для JS/htmx-хедеров)
    или из поля формы ``csrf_token``. FastAPI кэширует разобранную форму на
    request, поэтому повторное чтение ``Form(...)``/``UploadFile`` в самом роуте
    использует тот же кэш — двойного чтения тела не происходит.
    """
    cookie = request.cookies.get(CSRF_COOKIE)
    if not cookie:
        raise HTTPException(status_code=403, detail="CSRF: нет токена. Обновите страницу и повторите.")
    submitted = request.headers.get(HEADER)
    if submitted is None:
        form = await request.form()
        submitted = form.get(FORM_FIELD)
    if not submitted or not secrets.compare_digest(str(submitted), cookie):
        raise HTTPException(status_code=403, detail="CSRF: неверный токен. Обновите страницу и повторите.")
