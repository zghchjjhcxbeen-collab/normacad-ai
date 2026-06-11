"""Тесты валидации загрузок (Этап 1, безопасность)."""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.services.uploads import (  # noqa: E402
    content_matches_ext,
    looks_like_dxf,
    looks_like_pdf,
    sanitize_filename,
    unsupported_ext_message,
)


def test_magic_bytes():
    assert looks_like_pdf(b"%PDF-1.7\n%...")
    assert not looks_like_pdf(b"MZ\x90\x00")           # .exe
    assert looks_like_dxf(b"  0\r\nSECTION\r\n  2\r\nHEADER")
    assert not looks_like_dxf(b"MZ\x90\x00 not a dxf")
    assert content_matches_ext(b"%PDF-1.4 ...", ".pdf")
    assert not content_matches_ext(b"%PDF-1.4 ...", ".dxf")  # PDF выдают за DXF
    assert not content_matches_ext(b"random", ".png")        # неподдерживаемое


def test_unsupported_ext_message():
    dwg = unsupported_ext_message(".dwg")
    assert "DWG" in dwg and "DXF" in dwg           # честная подсказка про DWG → DXF
    assert unsupported_ext_message(".DWG") == dwg  # регистр расширения не важен
    assert ".dxf" in unsupported_ext_message(".png")  # прочее — общий список форматов


def test_sanitize_filename():
    assert sanitize_filename("../../etc/passwd") == "passwd"
    assert sanitize_filename(r"a\b\c.dxf").endswith(".dxf")
    assert sanitize_filename("чертёж (1).dxf") == "чертёж (1).dxf"
    assert sanitize_filename("Деталь_01.dxf") == "Деталь_01.dxf"
    assert sanitize_filename("") == "drawing"
