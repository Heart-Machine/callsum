"""Полный проход по одной записи: дорожки -> транскрипт -> протокол -> файлы."""

from __future__ import annotations

import json
import tempfile
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from . import audio, merge, naming, summarize
from .transcribe import Segment, Transcriber, hhmmss


class Result:
    def __init__(self, out_dir: Path):
        self.out_dir = out_dir
        self.transcript_md = out_dir / "transcript.md"
        self.transcript_srt = out_dir / "transcript.srt"
        self.transcript_json = out_dir / "transcript.json"
        self.summary_md = out_dir / "summary.md"


def _meta_block(src: Path, st: dict) -> str:
    when = datetime.fromtimestamp(src.stat().st_mtime).strftime("%Y-%m-%d %H:%M")
    shares = ", ".join(
        f"{name} — {info['share'] * 100:.0f}% ({hhmmss(info['seconds'])})"
        for name, info in sorted(st["per_speaker"].items(), key=lambda kv: -kv[1]["seconds"])
    )
    return (
        f"- Файл: {src.name}\n"
        f"- Дата записи: {when}\n"
        f"- Длительность: {hhmmss(st['duration'])}\n"
        f"- Участники и доля речи: {shares or '—'}"
    )


def _track_plan(src: Path, cfg, log) -> list[tuple[audio.Track, str]]:
    """Какие дорожки распознавать и под каким именем говорящего."""
    tracks = audio.probe_tracks(src)
    if not tracks:
        raise RuntimeError(f"В файле нет аудиодорожек: {src}")
    if len(tracks) == 1:
        return [(tracks[0], str(cfg.speakers.get("default", "Говорящий")))]
    plan = []
    for track in tracks:
        name = cfg.speaker_for_track(track.number)
        if track.title and track.title.lower() not in {"", "track1", "track2"}:
            log(f"  дорожка {track.number}: «{track.title}» -> {name}")
        plan.append((track, name))
    return plan


def process(
    src: Path,
    cfg,
    out_root: Path | None = None,
    do_summary: bool = True,
    keep_audio: bool = False,
    transcriber: Transcriber | None = None,
    log=print,
    on_stage=None,
) -> Result:
    """on_stage(stage, fraction, detail) — для UI: stage из
    {"audio", "transcribe", "summary", "done"}, fraction в [0, 1] или None."""
    src = Path(src).resolve()
    out_root = Path(out_root) if out_root else cfg.path("out")
    out_dir = out_root / naming.folder_name(src, cfg.paths.get("folder_template", ""))
    out_dir.mkdir(parents=True, exist_ok=True)
    res = Result(out_dir)

    log(f"\n=== {src.name} ===")
    total = audio.duration_seconds(src)
    log(f"Длительность: {hhmmss(total)}")
    stage = on_stage or (lambda *a, **kw: None)
    stage("audio", None, "готовлю дорожки")
    plan = _track_plan(src, cfg, log)

    tmp_ctx = None
    if keep_audio:
        work = out_dir / "audio"
        work.mkdir(exist_ok=True)
    else:
        tmp_ctx = tempfile.TemporaryDirectory(prefix="callsum_")
        work = Path(tmp_ctx.name)

    try:
        wavs: list[tuple[Path, str]] = []
        for track, speaker in plan:
            wav = audio.extract_track(src, track, work)
            if len(plan) > 1 and audio.is_silent(wav):
                log(f"  дорожка {track.number} ({speaker}) пустая — пропускаю")
                continue
            wavs.append((wav, speaker))
        if not wavs:
            raise RuntimeError("Все дорожки пустые — распознавать нечего")

        tr = transcriber or Transcriber(cfg)
        groups = []
        for i, (wav, speaker) in enumerate(wavs):
            # Прогресс считаем по всем дорожкам сразу, иначе полоса в UI
            # дважды бежит от нуля до конца на одном и том же созвоне.
            def report(frac, who, i=i):
                stage("transcribe", (i + frac) / len(wavs), who)

            groups.append(tr.run(wav, speaker, total, on_progress=report))
    finally:
        if tmp_ctx is not None:
            tmp_ctx.cleanup()

    segments = merge.merge_segments(groups, float(cfg.speakers.get("merge_gap", 2.0)))
    st = merge.stats(segments)
    meta = _meta_block(src, st)
    dialog = merge.to_dialog(segments)

    res.transcript_md.write_text(
        f"# Расшифровка: {src.stem}\n\n{meta}\n\n---\n\n{dialog}\n", encoding="utf-8"
    )
    res.transcript_srt.write_text(merge.to_srt(segments), encoding="utf-8")
    res.transcript_json.write_text(
        json.dumps(
            {"source": str(src), "stats": st, "segments": [asdict(s) for s in segments]},
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    log(f"Транскрипт: {res.transcript_md}")

    if do_summary and cfg.summary.get("enabled", True):
        stage("summary", None, "составляю протокол")
        try:
            text = summarize.summarize(dialog, meta, cfg, log=log)
            res.summary_md.write_text(
                f"# Протокол созвона: {src.stem}\n\n{meta}\n\n{text}\n", encoding="utf-8"
            )
            log(f"Протокол: {res.summary_md}")
        except summarize.OllamaError as exc:
            log(f"! Саммари не сделано: {exc}")
            log(f"  Транскрипт на месте — повторить можно так: callsum summarize \"{res.transcript_md}\"")
    stage("done", 1.0, str(res.out_dir))
    return res
