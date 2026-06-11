"""SVG-просмотрщик чертежа с подсветкой нарушений.

Главный вывод по PLAN (Этап 8) — РЕАЛЬНЫЙ лист с ошибками, отмеченными прямо на нём.
Поэтому для DXF рисуем настоящую геометрию через ezdxf SVG backend и накладываем рамки
замечаний (`render_real_dxf_svg`). Подсветку вписываем как DXF-сущности ДО рендера —
ezdxf проводит их через тот же трансформ, что и чертёж, так что рамки ложатся точно.

`render_schematic_svg` (рамка/штамп/точки текста) остаётся как запасной вариант —
для вектор-PDF и на случай, если реальный рендер не удался.

Ожидает ORM-объекты Finding (severity/ bbox/ rule_code/ message как атрибуты).
"""
from __future__ import annotations

import html
import re

_SEV_COLOR = {
    "critical": "#EF4444",
    "major": "#F59E0B",
    "minor": "#3B82F6",
    "info": "#10B981",
}

# ACI-цвета ezdxf для подсветки в реальном рендере: 1 красный, 30 оранжевый, 150 синий.
_SEV_ACI = {"critical": 1, "major": 1, "minor": 30, "info": 150}

_PDF_RENDER_DPI = 150  # растр листа PDF: баланс читаемости и веса data-URI


def _severity_str(f) -> str:
    """Severity у ORM-Finding — строка, у доменного — enum; привести к строке."""
    sev = getattr(f, "severity", "major")
    return getattr(sev, "value", sev)


def render_schematic_svg(parsed, findings) -> str:
    w, h = parsed.frame_size_mm or (420.0, 297.0)
    pad = max(w, h) * 0.04
    vb_w, vb_h = w + 2 * pad, h + 2 * pad

    out: list[str] = [
        f'<svg xmlns="http://www.w3.org/2000/svg" '
        f'viewBox="{-pad:.1f} {-pad:.1f} {vb_w:.1f} {vb_h:.1f}" '
        f'class="dxf-svg" preserveAspectRatio="xMidYMid meet">',
        f'<rect x="{-pad:.1f}" y="{-pad:.1f}" width="{vb_w:.1f}" height="{vb_h:.1f}" fill="#0B1220"/>',
        f'<g transform="translate(0,{h:.1f}) scale(1,-1)">',
        f'<rect x="0" y="0" width="{w:.1f}" height="{h:.1f}" fill="none" stroke="#9AA6BC" stroke-width="0.8"/>',
    ]

    tb = parsed.title_block
    if tb and tb.present and tb.bbox_mm:
        x, y, bw, bh = tb.bbox_mm
        out.append(
            f'<rect x="{x:.1f}" y="{y:.1f}" width="{bw:.1f}" height="{bh:.1f}" '
            f'fill="none" stroke="#6B7891" stroke-width="0.6"/>'
        )

    for t in parsed.texts:
        cx, cy = t.insert_mm
        out.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="1.6" fill="#3B82F6" opacity="0.6"/>')

    for f in findings:
        bb = getattr(f, "bbox", None)
        if not bb:
            continue
        x, y, bw, bh = bb
        color = _SEV_COLOR.get(getattr(f, "severity", "major"), "#F59E0B")
        label = f"{f.rule_code}: {f.message}"
        out.append(
            f'<g><rect x="{x:.1f}" y="{y:.1f}" width="{bw:.1f}" height="{bh:.1f}" '
            f'fill="{color}" fill-opacity="0.12" stroke="{color}" stroke-width="1.4"/>'
            f'<title>{html.escape(label)}</title></g>'
        )

    out.append("</g></svg>")
    return "".join(out)


# ---------- реальный рендер DXF (геометрия + подсветка ошибок на самом чертеже) ----------

def render_drawing_svg(path: str, parsed, findings) -> str:
    """Главная точка входа просмотрщика: реальный лист (DXF — ezdxf, PDF — растр), иначе схема."""
    fmt = getattr(parsed, "source_format", "dxf")
    if fmt == "dxf":
        try:
            return render_real_dxf_svg(path, findings, getattr(parsed, "mm_per_unit", 1.0) or 1.0)
        except Exception:
            pass  # деградация на схему, если реальный рендер не удался — отчёт не теряем
    elif fmt in ("vector_pdf", "raster_pdf"):
        try:
            return render_pdf_raster_svg(path, parsed, findings)
        except Exception:
            pass  # деградация на схему, если pypdfium2 не справился
    return render_schematic_svg(parsed, findings)


def render_real_dxf_svg(path: str, findings, mm_per_unit: float = 1.0) -> str:
    """Отрисовать реальный DXF (ezdxf SVG backend) и наложить рамки замечаний.

    Рамки замечаний вписываются как LWPOLYLINE на спец-слой ДО рендера → ezdxf проводит
    их через тот же трансформ, что и геометрию (точное совмещение). Координаты замечаний
    в мм → делим на mm_per_unit, чтобы вернуть в единицы чертежа.
    """
    import ezdxf
    from ezdxf.addons.drawing import Frontend, RenderContext
    from ezdxf.addons.drawing import layout as dl
    from ezdxf.addons.drawing.config import BackgroundPolicy, Configuration
    from ezdxf.addons.drawing.svg import SVGBackend

    doc = ezdxf.readfile(path)
    msp = doc.modelspace()

    inv = (1.0 / mm_per_unit) if mm_per_unit else 1.0
    if "_NCAD_HL" not in doc.layers:
        doc.layers.add("_NCAD_HL")
    width_u = 0.9 * inv  # ~0,9 мм толщина рамки подсветки в единицах чертежа
    for f in findings:
        bb = getattr(f, "bbox", None)
        if not bb:
            continue
        x, y, bw, bh = (v * inv for v in bb)
        if bw <= 0 or bh <= 0:
            continue
        aci = _SEV_ACI.get(getattr(f, "severity", "major"), 1)
        msp.add_lwpolyline(
            [(x, y), (x + bw, y), (x + bw, y + bh), (x, y + bh)],
            close=True,
            dxfattribs={"layer": "_NCAD_HL", "color": aci, "const_width": width_u},
        )

    ctx = RenderContext(doc)
    backend = SVGBackend()
    # Белый лист-«бумага»: ezdxf сам приводит базовые линии к чёрному, цветные слои
    # сохраняют цвет, а наша подсветка (красная/оранж/синяя) остаётся видимой.
    config = Configuration().with_changes(background_policy=BackgroundPolicy.WHITE)
    Frontend(ctx, backend, config=config).draw_layout(msp)
    return _svg_for_web(backend.get_string(dl.Page(0, 0, dl.Units.mm)))


# ---------- реальный рендер PDF (растр страницы + подсветка ошибок поверх) ----------

def render_pdf_raster_svg(path: str, parsed, findings) -> str:
    """Отрисовать страницу PDF растром (pypdfium2) и наложить рамки замечаний поверх.

    PDF рисуем картинкой страницы (data-URI PNG внутри SVG-<image>), а рамки замечаний
    кладём SVG-<rect>'ами в пиксельных координатах — те же подсказки по наведению, что и
    у DXF. Совмещение точное: обе системы привязаны к размеру страницы в мм
    (`parsed.frame_size_mm`); оси Y разные (в мм — снизу вверх, в растре — сверху вниз),
    поэтому верх рамки = H_мм − (y + h).
    """
    import base64
    import io

    import pypdfium2 as pdfium

    pdf = pdfium.PdfDocument(path)
    try:
        bitmap = pdf[0].render(scale=_PDF_RENDER_DPI / 72.0)
        image = bitmap.to_pil()
        img_w, img_h = image.size
        buf = io.BytesIO()
        image.save(buf, format="PNG")
    finally:
        pdf.close()
    b64 = base64.b64encode(buf.getvalue()).decode("ascii")

    w_mm, h_mm = parsed.frame_size_mm or (float(img_w), float(img_h))
    sx = img_w / w_mm if w_mm else 1.0
    sy = img_h / h_mm if h_mm else 1.0

    out: list[str] = [
        f'<svg xmlns="http://www.w3.org/2000/svg" '
        f'xmlns:xlink="http://www.w3.org/1999/xlink" class="dxf-svg" '
        f'viewBox="0 0 {img_w} {img_h}" preserveAspectRatio="xMidYMid meet">',
        f'<image x="0" y="0" width="{img_w}" height="{img_h}" '
        f'xlink:href="data:image/png;base64,{b64}" '
        f'href="data:image/png;base64,{b64}"/>',
    ]
    sw = max(img_w, img_h) * 0.0025  # толщина рамки подсветки ~пропорц. листу
    for f in findings:
        bb = getattr(f, "bbox", None)
        if not bb:
            continue
        x, y, bw, bh = bb
        if bw <= 0 or bh <= 0:
            continue
        px, py = x * sx, (h_mm - (y + bh)) * sy
        pw, ph = bw * sx, bh * sy
        color = _SEV_COLOR.get(_severity_str(f), "#F59E0B")
        label = f"{f.rule_code}: {f.message}"
        out.append(
            f'<g><rect x="{px:.1f}" y="{py:.1f}" width="{pw:.1f}" height="{ph:.1f}" '
            f'fill="{color}" fill-opacity="0.12" stroke="{color}" stroke-width="{sw:.2f}"/>'
            f'<title>{html.escape(label)}</title></g>'
        )
    out.append("</svg>")
    return "".join(out)


def _svg_for_web(svg: str) -> str:
    """Подготовить SVG ezdxf к встраиванию: убрать XML-пролог и физические мм-размеры
    (масштаб по контейнеру через CSS), добавить класс просмотрщика."""
    i = svg.find("<svg")
    if i > 0:
        svg = svg[i:]
    svg = re.sub(r'\s(?:width|height)="[^"]*mm"', "", svg, count=2)
    return svg.replace("<svg ", '<svg class="dxf-svg" ', 1)
