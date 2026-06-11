"""Маршрутизатор парсинга по формату файла (PLAN.md, Stage 0 — routing по формату).

Единая точка входа для воркера и веб-слоя: по расширению выбирает парсер и
возвращает общий ParsedDrawing. Импорты ленивые — чтобы не тянуть ezdxf при
обработке PDF и pdfplumber при обработке DXF.
"""
from __future__ import annotations

import pathlib

from app.domain import ParsedDrawing


def parse_drawing(path: str) -> ParsedDrawing:
    ext = pathlib.Path(path).suffix.lower()
    if ext == ".pdf":
        from app.parsing.pdf import parse_pdf
        return parse_pdf(path)
    from app.parsing.dxf import parse_dxf
    return parse_dxf(path)
