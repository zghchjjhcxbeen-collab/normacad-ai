"""Исполнение задач проверки.

План B (dev): обработка в фоне через FastAPI BackgroundTasks (in-process).
В проде enqueue() переключается на ARQ/Redis — бизнес-логика process_job не меняется.
"""
from __future__ import annotations

import asyncio

from app.db import SessionLocal, utcnow
from app.models import CheckJob, Drawing, Finding, Report
from app.parsing.dispatch import parse_drawing
from app.rules.engine import run_checks, summarize


def enqueue(background_tasks, job_id: int) -> None:
    """Точка подмены на ARQ в будущем."""
    background_tasks.add_task(process_job, job_id)


def _summary_text(counts: dict) -> str:
    total = sum(counts.values())
    if total == 0:
        return "Замечаний не найдено по проверкам Этапа 0."
    parts = []
    labels = {"critical": "ошибок", "major": "серьёзных", "minor": "мелких", "info": "рекомендаций"}
    for key, label in labels.items():
        if counts.get(key):
            parts.append(f"{counts[key]} {label}")
    return "Найдено: " + ", ".join(parts) + "."


async def process_job(job_id: int) -> None:
    async with SessionLocal() as db:
        job = await db.get(CheckJob, job_id)
        if job is None or job.status not in ("queued", "running"):
            return
        job.status = "running"
        job.started_at = utcnow()
        await db.commit()

        try:
            drawing = await db.get(Drawing, job.drawing_id)
            parsed = await asyncio.to_thread(parse_drawing, drawing.stored_path)
            drawing.format_class = parsed.source_format
            findings = run_checks(parsed)
            s = summarize(findings)

            for f in findings:
                db.add(Finding(
                    job_id=job.id,
                    rule_code=f.rule_code,
                    rule_clause=f.rule_clause,
                    rule_title=f.rule_title,
                    severity=f.severity.value,
                    message=f.message,
                    fix=f.fix,
                    page=f.page,
                    bbox=list(f.bbox) if f.bbox else None,
                    handle=f.handle,
                    source=f.source.value,
                    confidence=f.confidence,
                ))
            db.add(Report(
                job_id=job.id,
                total_findings=s["total_findings"],
                counts_by_severity=s["counts_by_severity"],
                ruleset_version=s["ruleset_version"],
                summary=_summary_text(s["counts_by_severity"]),
            ))
            job.units_assumed = parsed.units_assumed
            job.ruleset_version = s["ruleset_version"]
            job.status = "done"
            job.finished_at = utcnow()
            await db.commit()
        except Exception as exc:  # noqa: BLE001 — любую ошибку парсинга фиксируем как failed
            await db.rollback()
            job = await db.get(CheckJob, job_id)
            if job is not None:
                job.status = "failed"
                job.error_text = f"{type(exc).__name__}: {exc}"
                job.finished_at = utcnow()
                await db.commit()
