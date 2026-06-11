"""Rate-limit в памяти процесса (Этап 1, безопасность).

План B = один процесс (uvicorn без воркеров), поэтому достаточно скользящего
окна на словаре. В проде с несколькими воркерами счётчик общий — вынести в
Redis (INCR + EXPIRE), интерфейс ``hit()`` при этом не меняется.

Доступ из async-роутов идёт в одном потоке цикла событий, между проверкой и
записью нет ``await`` — гонок нет, блокировка не нужна.
"""
from __future__ import annotations

import time
from collections import defaultdict, deque


class RateLimiter:
    def __init__(self, max_hits: int, window_seconds: float) -> None:
        self.max_hits = max_hits
        self.window = window_seconds
        self._hits: dict[str, deque[float]] = defaultdict(deque)

    def allow(self, key: str) -> bool:
        """Зарегистрировать попытку. True — в пределах лимита, False — превышен."""
        now = time.monotonic()
        cutoff = now - self.window
        dq = self._hits[key]
        while dq and dq[0] < cutoff:
            dq.popleft()
        if len(dq) >= self.max_hits:
            return False
        dq.append(now)
        return True

    def reset(self, key: str) -> None:
        """Сбросить счётчик (напр. после успешного входа — не наказывать легитимного)."""
        self._hits.pop(key, None)

    def retry_after(self, key: str) -> int:
        """Через сколько секунд освободится слот (для заголовка Retry-After)."""
        dq = self._hits.get(key)
        if not dq:
            return 0
        return max(0, int(self.window - (time.monotonic() - dq[0])) + 1)


# Логин: 10 попыток за 5 минут на (IP+email) — гасит брутфорс, не мешает человеку.
login_limiter = RateLimiter(max_hits=10, window_seconds=300)
# Загрузки: 30 файлов за 5 минут на пользователя — защита от заваливания воркера.
upload_limiter = RateLimiter(max_hits=30, window_seconds=300)
