"""Валидация загрузок (Этап 1, безопасность): сигнатура содержимого + имя файла.

Проверка расширения недостаточна: вредонос можно переименовать в `.dxf`. Здесь
сверяем СОДЕРЖИМОЕ (magic bytes) и обезвреживаем имя (путь/опасные символы).
"""
from __future__ import annotations

import pathlib
import re


def looks_like_pdf(content: bytes) -> bool:
    return content[:5] == b"%PDF-"


def looks_like_dxf(content: bytes) -> bool:
    head = content[:2048]
    if head.startswith(b"AutoCAD Binary DXF"):  # бинарный DXF
        return True
    # ASCII DXF: в начале файла обязательно присутствует метка SECTION
    return b"SECTION" in head


def content_matches_ext(content: bytes, ext: str) -> bool:
    """Похоже ли содержимое на заявленное расширение."""
    if ext == ".pdf":
        return looks_like_pdf(content)
    if ext == ".dxf":
        return looks_like_dxf(content)
    return False


# Подсказки по форматам, которые мы осознанно НЕ парсим офлайн — честная деградация
# вместо общего отказа (PLAN.md, Этап 7: «загрузите DXF/PDF»). DWG нельзя надёжно
# разобрать без лицензионного конвертера (см. docs/dwg-spike.md).
_FORMAT_HINTS = {
    ".dwg": ("Формат DWG напрямую пока не поддерживается: надёжно разобрать его офлайн без "
             "лицензионного конвертера нельзя. Экспортируйте чертёж в DXF или вектор-PDF из "
             "вашего CAD (AutoCAD/КОМПАС/nanoCAD/SolidWorks) — это пара кликов."),
    ".dwf": ("Формат DWF (просмотровый) не поддерживается. Экспортируйте чертёж в DXF или "
             "вектор-PDF из вашего CAD."),
}


def unsupported_ext_message(ext: str) -> str:
    """Сообщение об отклонении по расширению.

    Для известных CAD-форматов (DWG/DWF) — конкретная подсказка, как получить
    поддерживаемый файл; иначе — общий список допустимых форматов.
    """
    return _FORMAT_HINTS.get(ext.lower(), "Принимаются файлы .dxf и вектор-PDF (.pdf).")


_SAFE_NAME_RE = re.compile(r"[^A-Za-zА-Яа-яЁё0-9 ._()\-]+")


def sanitize_filename(name: str) -> str:
    """Обезвредить имя: убрать путь и опасные символы, ограничить длину."""
    base = pathlib.PurePosixPath((name or "").replace("\\", "/")).name
    base = _SAFE_NAME_RE.sub("_", base).strip(" .")
    return (base or "drawing")[:120]
