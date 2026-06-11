"""Доменные объекты ядра нормоконтроля.

Здесь нет зависимостей от FastAPI/SQLAlchemy — это «чистый» слой, который
одинаково используется и демо-скриптом, и будущим ARQ-воркером, и тестами.

Архитектурный инвариант (см. PLAN.md, раздел 4): каждое нарушение несёт
ЛОКАТОР места на чертеже — handle примитива (для вектора) и/или bbox.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional


class Severity(str, Enum):
    """Градация по PLAN.md: не «нарушение», а ошибка/предупреждение/рекомендация."""

    CRITICAL = "critical"   # ошибка — документ нельзя выпускать
    MAJOR = "major"         # серьёзное замечание
    MINOR = "minor"         # мелкое замечание
    INFO = "info"           # рекомендация / к сведению


class Source(str, Enum):
    """Откуда пришло замечание. Числа и факты — только из DETERMINISTIC."""

    DETERMINISTIC = "deterministic"
    VISION = "vision"
    MANUAL = "manual"  # добавлено нормоконтролёром (пропущено ИИ) — сигнал false-negative


@dataclass(slots=True)
class Finding:
    """Одно замечание нормоконтроля."""

    rule_code: str            # напр. "ГОСТ 2.304-81"
    rule_clause: str          # пункт актуальной редакции, напр. "п. 1.1"
    rule_title: str           # человекочитаемое название правила
    severity: Severity
    message: str              # что именно не так (с конкретикой)
    fix: str = ""             # как исправить (рекомендация для пользователя)
    source: Source = Source.DETERMINISTIC
    page: int = 1
    bbox: Optional[tuple[float, float, float, float]] = None  # (x, y, w, h) в мм
    handle: Optional[str] = None       # handle примитива ezdxf
    confidence: Optional[float] = None  # заполняется только для source=vision

    def as_dict(self) -> dict:
        return {
            "rule_code": self.rule_code,
            "rule_clause": self.rule_clause,
            "rule_title": self.rule_title,
            "severity": self.severity.value,
            "message": self.message,
            "fix": self.fix,
            "source": self.source.value,
            "page": self.page,
            "bbox": list(self.bbox) if self.bbox else None,
            "handle": self.handle,
            "confidence": self.confidence,
        }


@dataclass(slots=True)
class TextItem:
    """Текстовый примитив (TEXT/MTEXT), приведённый к миллиметрам."""

    text: str
    height_mm: float
    insert_mm: tuple[float, float]
    handle: str
    style: str = ""


@dataclass(slots=True)
class TitleBlock:
    """Основная надпись (штамп) по ГОСТ 2.104."""

    present: bool
    attribs: dict[str, str] = field(default_factory=dict)  # {ТЕГ_В_ВЕРХНЕМ_РЕГИСТРЕ: значение}
    bbox_mm: Optional[tuple[float, float, float, float]] = None
    block_handle: Optional[str] = None
    # Можно ли доверять значениям граф (attribs). True — штамп размечен тегами (ATTRIB/PDF).
    # False — штамп распознан по геометрии/подписям (реальные чертежи без ATTRIB): значения
    # граф надёжно не считать → заполненность граф не проверяем, только факт наличия.
    values_reliable: bool = True

    def graph(self, *tag_substrings: str) -> Optional[str]:
        """Вернуть значение графы, чей тег содержит одну из подстрок (регистронезависимо)."""
        for tag, value in self.attribs.items():
            for sub in tag_substrings:
                if sub.upper() in tag:
                    return value
        return None


@dataclass(slots=True)
class ParsedDrawing:
    """Результат парсинга чертежа — вход для движка правил."""

    source_path: str
    units_code: int                      # значение $INSUNITS
    units_assumed: bool                  # True, если единицы пришлось предположить (0/unitless)
    mm_per_unit: float                   # множитель «единица чертежа → мм»
    frame_size_mm: Optional[tuple[float, float]]  # (ширина, высота) габарита/рамки в мм
    texts: list[TextItem] = field(default_factory=list)
    title_block: Optional[TitleBlock] = None
    warnings: list[str] = field(default_factory=list)
    source_format: str = "dxf"  # dxf | vector_pdf | raster_pdf — движок ветвит проверки
    line_weights_mm: list[float] = field(default_factory=list)  # явные толщины линий (мм) — для 2.303
    # Надёжны ли измеренные высоты шрифта (height_mm у texts). Для DXF — да (высота точна).
    # Для PDF — да только если cap-height посчитан по известной метрике шрифта (см. parsing/pdf.py);
    # иначе 2.304 уходит в «ручную проверку», чтобы не выдавать ложные замечания.
    font_metrics_reliable: bool = True
