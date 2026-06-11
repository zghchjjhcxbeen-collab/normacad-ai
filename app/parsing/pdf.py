"""Парсер born-digital (вектор) PDF → ParsedDrawing (тот же контракт, что у DXF).

Этап 4 (PLAN.md): принимаем вектор-PDF тем же движком правил. Извлекаем то, что
НАДЁЖНО:
- формат листа из размеров страницы (MediaBox/CropBox) → мм — для ГОСТ 2.301;
- масштаб из текста штампа (правый нижний угол) — для ГОСТ 2.302;
- высоту ПРОПИСНЫХ букв (cap-height) для ГОСТ 2.304 — но только если метрика шрифта
  известна (см. ниже), иначе правило уходит в «ручную проверку».

Про высоту шрифта (важно для точности). pdfplumber отдаёт для символа `size` (кегль,
em) и bbox, причём высота bbox символа РАВНА кеглю — это НЕ высота прописных. ГОСТ
2.304 нормирует высоту прописных h. Поэтому h = em × cap-ratio, где cap-ratio — доля
высоты заглавной буквы в em у конкретного шрифта (CapHeight/1000).

Источник cap-ratio (по приоритету точности):
1. РЕАЛЬНЫЙ CapHeight из дескриптора встроенного шрифта (`/FontDescriptor /CapHeight`,
   pdfminer.six) — точное значение именно для этого шрифта (`_embedded_cap_ratios`);
2. таблица AFM-метрик стандартных шрифтов `_CAP_RATIOS` (если шрифт не встроен/без
   CapHeight, но известен по имени);
3. иначе метрика помечается ненадёжной, и 2.304 на таком PDF не проверяется — уходит в
   «ручную проверку» (точность важнее охвата → без ложных срабатываний).

Чего НЕ делаем на PDF в этом срезе: заполненность граф штампа 2.104 (нужна надёжная
реконструкция таблицы штампа — отдельный шаг, нужен реальный корпус) — «ручная проверка».

Инвариант сохраняется: числа считаются из геометрии/метрик PDF, не из LLM.
Скан/растровый PDF (нет текстового слоя) сюда не идёт — помечается raster_pdf.
"""
from __future__ import annotations

import re

import pdfplumber

from app.domain import ParsedDrawing, TextItem, TitleBlock
from app.ruleset import TITLE_BLOCK_SIZE_MM

_PT_TO_MM = 25.4 / 72.0
_MIN_VECTOR_CHARS = 15  # меньше — считаем скан/растр (нет извлекаемого текста)

# cap-ratio = CapHeight/1000 (доля высоты прописной в em). Значения из AFM-метрик
# стандартных шрифтов. Ключ — подстрока имени шрифта (lowercase, после префикса
# subset 'ABCDEF+'). Шрифт не из таблицы → метрика ненадёжна, 2.304 уходит в ручную.
_CAP_RATIOS: dict[str, float] = {
    "helvetica": 0.718, "arial": 0.716, "liberationsans": 0.716,
    "times": 0.662, "georgia": 0.692,
    "courier": 0.571, "consolas": 0.700,
    "isocp": 0.700, "isocpeur": 0.700, "iso": 0.700,  # чертёжные ISO-шрифты
    "gost": 0.700, "gosttype": 0.700,                  # ГОСТ тип А/Б ≈ 0.70
}
_CAP_RATIO_FALLBACK = 0.70  # для неизвестного шрифта (метрика помечается ненадёжной)


def _cap_ratio(
    fontname: str | None, embedded: dict[str, float] | None = None
) -> tuple[float, bool]:
    """Вернуть (cap-ratio, надёжна_ли). Имя шрифта может иметь subset-префикс 'ABCDEF+'.

    Приоритет: реальный CapHeight из дескриптора встроенного шрифта (`embedded`) →
    таблица стандартных метрик `_CAP_RATIOS` → ненадёжный fallback.
    """
    fn = (fontname or "").split("+")[-1].lower()
    if embedded:
        # точное значение из дескриптора ИМЕННО этого шрифта (важнее усреднённой таблицы)
        if fn in embedded:
            return embedded[fn], True
        for key, ratio in embedded.items():
            if key and key in fn:
                return ratio, True
    for key, ratio in _CAP_RATIOS.items():
        if key in fn:
            return ratio, True
    return _CAP_RATIO_FALLBACK, False


def _embedded_cap_ratios(path: str) -> dict[str, float]:
    """Прочитать CapHeight из дескрипторов встроенных шрифтов 1-й страницы → {имя: ratio}.

    CapHeight в дескрипторе задаётся в glyph-space (1/1000 em), поэтому ratio =
    CapHeight/1000 — точная доля высоты прописной для этого шрифта (точнее таблицы).
    Ключ — имя шрифта без subset-префикса 'ABCDEF+', lowercase (как у pdfplumber-слова).
    Нет дескриптора/CapHeight (или ≤0) — шрифт в карту не попадает (откат на таблицу).
    Любой сбой парсинга проглатывается: точность здесь — бонус, не обязательное условие.
    """
    from pdfminer.pdfdocument import PDFDocument
    from pdfminer.pdfpage import PDFPage
    from pdfminer.pdfparser import PDFParser
    from pdfminer.pdftypes import resolve1
    from pdfminer.psparser import PSLiteral

    def _name(v) -> str:
        v = resolve1(v)
        if isinstance(v, PSLiteral):
            v = v.name
        if isinstance(v, bytes):
            v = v.decode("latin-1", "ignore")
        return str(v or "")

    ratios: dict[str, float] = {}
    try:
        with open(path, "rb") as fh:
            doc = PDFDocument(PDFParser(fh))
            pages = PDFPage.create_pages(doc)
            page = next(pages, None)
            if page is None:
                return {}
            fonts = resolve1((resolve1(page.resources) or {}).get("Font")) or {}
            if not isinstance(fonts, dict):
                return {}
            for font_ref in fonts.values():
                font = resolve1(font_ref)
                if not isinstance(font, dict):
                    continue
                base = _name(font.get("BaseFont"))
                desc = resolve1(font.get("FontDescriptor"))
                if desc is None:  # Type0/CID: дескриптор лежит у потомка
                    dfs = resolve1(font.get("DescendantFonts"))
                    if isinstance(dfs, list) and dfs:
                        child = resolve1(dfs[0])
                        if isinstance(child, dict):
                            base = base or _name(child.get("BaseFont"))
                            desc = resolve1(child.get("FontDescriptor"))
                if not isinstance(desc, dict):
                    continue
                try:
                    cap = float(resolve1(desc.get("CapHeight")))
                except (TypeError, ValueError):
                    continue
                key = base.split("+")[-1].lower()
                if key and cap > 0:
                    ratios[key] = round(cap / 1000.0, 4)
    except Exception:  # noqa: BLE001 — точность опциональна, не валим парсинг PDF
        return {}
    return ratios
# кандидат масштаба: 1:2, 1:2,5, 2:1 (разделители-пробелы допустимы — текст из штампа)
_SCALE_TOKEN_RE = re.compile(r"\d+(?:[.,]\d+)?\s*:\s*\d+(?:[.,]\d+)?")


def classify_pdf(path: str) -> str:
    """Грубая классификация первой страницы: 'vector' | 'raster' | 'empty'."""
    with pdfplumber.open(path) as pdf:
        if not pdf.pages:
            return "empty"
        page = pdf.pages[0]
        return "vector" if len(page.chars) >= _MIN_VECTOR_CHARS else "raster"


def _raster_result(path: str, reason: str) -> ParsedDrawing:
    return ParsedDrawing(
        source_path=path,
        units_code=-1,
        units_assumed=False,
        mm_per_unit=round(_PT_TO_MM, 6),
        frame_size_mm=None,
        texts=[],
        title_block=TitleBlock(present=False),
        warnings=[
            f"{reason} Точные проверки растра — растровый тракт (Этап 5). "
            f"Загрузите вектор-PDF (born-digital) или DXF."
        ],
        source_format="raster_pdf",
    )


def _looks_like_scale(token: str) -> bool:
    """Похоже ли на масштаб ЕСКД: одна сторона = 1, обе ≤ 1000.

    Отсекает ложные «число:число» из штампа (даты, номера вида 2024:01, время 12:30),
    которые иначе дали бы ложное замечание 2.302. Весь ряд ГОСТ 2.302 имеет «1» с
    одной стороны и величину ≤ 1000.
    """
    m = re.search(r"(\d+(?:[.,]\d+)?)\s*:\s*(\d+(?:[.,]\d+)?)", token)
    if not m:
        return False
    try:
        a = float(m.group(1).replace(",", "."))
        b = float(m.group(2).replace(",", "."))
    except ValueError:
        return False
    if a <= 0 or b <= 0 or max(a, b) > 1000:
        return False
    return a == 1 or b == 1


def _detect_scale_block(texts: list[TextItem], w_mm: float, h_mm: float) -> TitleBlock:
    """Масштаб ищем ТОЛЬКО в зоне штампа (правый-нижний угол) и валидируем форму.

    Точность важнее охвата: лучше не проверить масштаб (нет замечания), чем выдать
    ложное. Если штамп не в правом-нижнем углу — масштаб на PDF не проверяется.
    """
    right_bottom = [
        t for t in texts
        if t.insert_mm[0] >= 0.5 * w_mm and t.insert_mm[1] <= 0.35 * h_mm
    ]
    joined = " ".join(t.text for t in right_bottom)
    scale = next(
        (m.group(0) for m in _SCALE_TOKEN_RE.finditer(joined) if _looks_like_scale(m.group(0))),
        None,
    )
    if scale is None:
        return TitleBlock(present=False)
    bw, bh = TITLE_BLOCK_SIZE_MM
    return TitleBlock(
        present=True,
        attribs={"МАСШТАБ": scale},
        bbox_mm=(round(max(w_mm - bw, 0.0), 2), 0.0, bw, bh),
        block_handle=None,
    )


def parse_pdf(path: str) -> ParsedDrawing:
    """Распарсить вектор-PDF в ParsedDrawing. Скан → source_format='raster_pdf'.

    Бросает исключение pdfplumber при битом файле — воркер ловит и помечает failed.
    """
    with pdfplumber.open(path) as pdf:
        if not pdf.pages:
            return _raster_result(path, "PDF без страниц.")
        n_pages = len(pdf.pages)
        page = pdf.pages[0]
        if len(page.chars) < _MIN_VECTOR_CHARS:
            return _raster_result(path, "В PDF нет извлекаемого текстового слоя (вероятно скан).")
        page_w, page_h = float(page.width), float(page.height)
        # extra_attrs тянет кегль и имя шрифта в каждое слово (слово дробится при их смене)
        words = page.extract_words(
            use_text_flow=False, keep_blank_chars=False,
            extra_attrs=["size", "fontname"],
        ) or []

    warnings: list[str] = []
    if n_pages > 1:
        warnings.append(f"PDF содержит {n_pages} стр.; в этом срезе проверяется только первая (стр. 1).")

    w_mm = round(page_w * _PT_TO_MM, 2)
    h_mm = round(page_h * _PT_TO_MM, 2)

    # реальные CapHeight из дескрипторов встроенных шрифтов (приоритет над таблицей)
    embedded = _embedded_cap_ratios(path)

    # координаты приводим к мм и оси Y «вверх» (как DXF): y = высота_страницы - low
    texts: list[TextItem] = []
    metrics_reliable = True  # станет False, если встретился шрифт без известной метрики
    for i, wd in enumerate(words):
        x_mm = round(float(wd["x0"]) * _PT_TO_MM, 2)
        y_mm = round((page_h - float(wd["bottom"])) * _PT_TO_MM, 2)
        # высота ПРОПИСНЫХ = кегль(em) × cap-ratio шрифта. Если size нет — bbox как запас.
        em_pt = float(wd.get("size") or (float(wd["bottom"]) - float(wd["top"])))
        ratio, ok = _cap_ratio(wd.get("fontname"), embedded)
        if not ok:
            metrics_reliable = False
        cap_mm = round(em_pt * ratio * _PT_TO_MM, 3)
        texts.append(TextItem(
            text=str(wd["text"]),
            height_mm=cap_mm,
            insert_mm=(x_mm, y_mm),
            handle=f"pdf-w{i}",
            style=str(wd.get("fontname") or "pdf"),
        ))

    return ParsedDrawing(
        source_path=path,
        units_code=-1,
        units_assumed=False,
        mm_per_unit=round(_PT_TO_MM, 6),
        frame_size_mm=(w_mm, h_mm),
        texts=texts,
        title_block=_detect_scale_block(texts, w_mm, h_mm),
        warnings=warnings,
        source_format="vector_pdf",
        font_metrics_reliable=metrics_reliable,
    )
