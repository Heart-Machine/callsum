"""Слежение за папкой записей: новые файлы обрабатываются автоматически."""

from __future__ import annotations

import time
from pathlib import Path

from . import naming
from .pipeline import process
from .transcribe import Transcriber


def _candidates(folder: Path, exts: set[str]) -> list[Path]:
    return sorted(
        p for p in folder.rglob("*")
        if p.is_file() and p.suffix.lower() in exts and not p.name.startswith(".")
    )


def _is_done(src: Path, out_root: Path, template: str = "") -> bool:
    return (naming.result_dir(src, out_root, template) / "transcript.md").exists()


def _is_stable(src: Path, seconds: float) -> bool:
    """Файл дописан: OBS ещё пишет — размер растёт, mtime свежий."""
    try:
        stat = src.stat()
    except OSError:
        return False
    return stat.st_size > 0 and (time.time() - stat.st_mtime) >= seconds


def run(cfg, once: bool = False, interval: float = 10.0, do_summary: bool = True, log=print) -> None:
    folder = cfg.path("recordings")
    out_root = cfg.path("out")
    folder.mkdir(parents=True, exist_ok=True)
    out_root.mkdir(parents=True, exist_ok=True)
    exts = {e.lower() for e in cfg.audio["extensions"]}
    stable = float(cfg.audio["stable_seconds"])

    log(f"Слежу за папкой: {folder}")
    log(f"Результаты: {out_root}")
    if not once:
        log("Останов — Ctrl+C.\n")

    transcriber: Transcriber | None = None
    while True:
        for src in _candidates(folder, exts):
            if _is_done(src, out_root, cfg.paths.get("folder_template", "")):
                continue
            if not _is_stable(src, stable):
                log(f"…{src.name} ещё пишется, жду")
                continue
            try:
                if transcriber is None:
                    transcriber = Transcriber(cfg)
                process(src, cfg, do_summary=do_summary, transcriber=transcriber, log=log)
            except Exception as exc:  # noqa: BLE001 — одна битая запись не должна ронять службу
                log(f"! Ошибка на {src.name}: {exc}; повторю на следующем обходе")
        if once:
            return
        time.sleep(interval)
