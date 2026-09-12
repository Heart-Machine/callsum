"""Слияние реплик с разных дорожек в один диалог по таймкодам."""

from __future__ import annotations

from .transcribe import Segment, hhmmss


def merge_segments(groups: list[list[Segment]], gap: float = 2.0) -> list[Segment]:
    """Свести реплики всех дорожек в хронологический диалог.

    Соседние реплики одного говорящего склеиваются, если пауза между ними
    меньше `gap` — иначе Whisper режет фразу на куски по 5-10 секунд.
    """
    flat = sorted((s for g in groups for s in g), key=lambda s: (s.start, s.end))
    merged: list[Segment] = []
    for seg in flat:
        prev = merged[-1] if merged else None
        if prev and prev.speaker == seg.speaker and seg.start - prev.end <= gap:
            prev.end = max(prev.end, seg.end)
            prev.text = f"{prev.text} {seg.text}".strip()
        else:
            merged.append(Segment(seg.start, seg.end, seg.text, seg.speaker))
    return merged


def to_dialog(segments: list[Segment], with_time: bool = True) -> str:
    """Транскрипт в виде «[00:01:23] Собеседник: текст»."""
    lines = []
    for seg in segments:
        stamp = f"[{hhmmss(seg.start)}] " if with_time else ""
        lines.append(f"{stamp}{seg.speaker}: {seg.text}")
    return "\n".join(lines)


def to_srt(segments: list[Segment]) -> str:
    def ts(value: float) -> str:
        ms = int(round(value * 1000))
        h, ms = divmod(ms, 3_600_000)
        m, ms = divmod(ms, 60_000)
        s, ms = divmod(ms, 1000)
        return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"

    blocks = []
    for i, seg in enumerate(segments, 1):
        blocks.append(f"{i}\n{ts(seg.start)} --> {ts(seg.end)}\n{seg.speaker}: {seg.text}\n")
    return "\n".join(blocks)


def stats(segments: list[Segment]) -> dict:
    """Кто сколько говорил — полезная строка в шапке отчёта."""
    per_speaker: dict[str, float] = {}
    for seg in segments:
        per_speaker[seg.speaker] = per_speaker.get(seg.speaker, 0.0) + (seg.end - seg.start)
    total = sum(per_speaker.values()) or 1.0
    return {
        "duration": max((s.end for s in segments), default=0.0),
        "speech": total,
        "per_speaker": {k: {"seconds": v, "share": v / total} for k, v in per_speaker.items()},
        "lines": len(segments),
        "chars": sum(len(s.text) for s in segments),
    }
