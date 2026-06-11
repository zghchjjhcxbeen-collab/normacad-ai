"""Генератор бутстрап-набора вектор-PDF (Этап 4).

Делает born-digital PDF (с текстовым слоем) для проверки PDF-тракта тем же движком:
- clean_a3.pdf  — A3 (≈420×297), масштаб 1:2 (в ряду), весь текст 14pt Helvetica
  (cap-height ≈ 3,55 мм — в ряду 2.304) → формат/масштаб/шрифт OK;
- dirty_a3.pdf  — нестандартный формат (≈400×300, 2.301), масштаб 1:7 вне ряда (2.302)
  и мелкий 6pt текст (cap-height ≈ 1,52 мм < 2,5 → нарушение 2.304).

Текст — латиницей (Helvetica): ядро PDF-шрифтов fpdf2 без кириллицы. Helvetica — шрифт
с известной AFM-метрикой (CapHeight 718), поэтому высота прописных меряется надёжно;
проверкам важны размер страницы, токен масштаба и кегль, содержимое надписей вторично.
Это синтетика, не реальный корпус — метрики предварительны (см. PLAN.md).
"""
from __future__ import annotations

import pathlib
import sys

from fpdf import FPDF

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "data" / "bootstrap"


def _draw(pdf: FPDF, scale_text: str, tiny: bool = False) -> None:
    w, h = pdf.w, pdf.h
    pdf.set_line_width(0.4)
    pdf.rect(10, 10, w - 20, h - 20)  # внешняя рамка
    # Весь основной текст 14pt → cap-height ≈ 3,55 мм (в ряду 2.304).
    pdf.set_font("helvetica", size=14)
    pdf.text(20, 30, "Tehnicheskie trebovaniya")
    pdf.text(20, 45, "1. Razmery dlya spravok.")
    # штамп: правый-нижний угол (зона, где парсер ищет масштаб), тоже 14pt
    pdf.text(w - 95, h - 22, "Naimenovanie: Val")
    pdf.text(w - 95, h - 14, "ABVG.301261.001")
    pdf.text(w - 95, h - 6, f"Masshtab {scale_text}")
    if tiny:
        # Заведомо мелкий текст 6pt → cap-height ≈ 1,52 мм < 2,5 → нарушение ГОСТ 2.304.
        pdf.set_font("helvetica", size=6)
        pdf.text(20, 60, "melkiy nechitaemyy text 6pt")


def build_clean(path: str) -> None:
    pdf = FPDF(orientation="L", unit="mm", format="A3")  # ≈420×297 мм
    pdf.add_page()
    _draw(pdf, "1:2")
    pdf.output(path)


def build_dirty(path: str) -> None:
    pdf = FPDF(orientation="P", unit="mm", format=(400, 300))  # нестандартный формат
    pdf.add_page()
    _draw(pdf, "1:7", tiny=True)
    pdf.output(path)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    build_clean(str(OUT / "clean_a3.pdf"))
    build_dirty(str(OUT / "dirty_a3.pdf"))
    print(f"Создан: {OUT / 'clean_a3.pdf'}")
    print(f"Создан: {OUT / 'dirty_a3.pdf'}")


if __name__ == "__main__":
    main()
