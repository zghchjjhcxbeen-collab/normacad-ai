"""Тесты поиска по базе ГОСТов (пропускаются, если база недоступна на машине)."""
from __future__ import annotations

import pathlib
import sys

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app import gostdb  # noqa: E402
from app.ruleset import RULE_META  # noqa: E402


def test_search_font_finds_2304():
    if not gostdb.all_gosts():
        pytest.skip("база ГОСТов недоступна на этой машине")
    hits = gostdb.search("минимальная высота чертёжного шрифта")
    numbers = {g["number"] for g in hits}
    assert any("2.304" in n for n in numbers), numbers


def test_by_number():
    if not gostdb.all_gosts():
        pytest.skip("база ГОСТов недоступна на этой машине")
    g = gostdb.by_number("ГОСТ 2.301-68")
    assert g is not None and g["number"] == "ГОСТ 2.301-68"


def test_grounding_quote_2304():
    if not gostdb.all_gosts():
        pytest.skip("база ГОСТов недоступна на этой машине")
    g = gostdb.grounding_quote("ГОСТ 2.304-81", ("Размер шрифта h", "высотой прописных букв"))
    assert g is not None
    assert "прописных" in g["quote"].lower()
    assert g["number"] == "ГОСТ 2.304-81"


def test_grounding_quote_unknown_returns_none():
    # анти-галлюцинация: нет якоря в тексте → None, а не выдуманная цитата
    assert gostdb.grounding_quote("ГОСТ 2.304-81", ("заведомо отсутствующая фраза щъ",)) is None


def test_all_p0_rules_grounded():
    """Каждое P0-правило обязано иметь обоснование, извлекаемое прямо из базы."""
    if not gostdb.all_gosts():
        pytest.skip("база ГОСТов недоступна на этой машине")
    for key, m in RULE_META.items():
        g = gostdb.grounding_quote(m["code"], m.get("ground_anchors", ()))
        assert g is not None, f"нет обоснования для {key} ({m['code']})"
        assert len(g["quote"]) >= 40, f"слишком короткая цитата для {key}"
