"""Работа с аудио: разбор дорожек и извлечение их в 16 кГц моно WAV."""

from __future__ import annotations

import json
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path


class FFmpegMissing(RuntimeError):
    pass


def _tool(name: str) -> str:
    exe = shutil.which(name)
    if not exe:
        raise FFmpegMissing(
            f"Не найден {name}. Установите FFmpeg и добавьте его в PATH: winget install Gyan.FFmpeg"
        )
    return exe


@dataclass
class Track:
    """Одна аудиодорожка внутри файла записи."""

    index: int          # порядковый номер среди аудиопотоков, с 0
    title: str          # tags:title из контейнера (OBS пишет туда имя дорожки)
    language: str
    channels: int

    @property
    def number(self) -> int:
        """Номер дорожки в человеческой нумерации (как в настройках OBS)."""
        return self.index + 1


def probe_tracks(src: Path) -> list[Track]:
    """Список аудиодорожек файла."""
    cmd = [
        _tool("ffprobe"), "-v", "error", "-select_streams", "a",
        "-show_entries", "stream=index,channels:stream_tags=title,language",
        "-of", "json", str(src),
    ]
    raw = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", check=True).stdout
    streams = json.loads(raw).get("streams", [])
    tracks = []
    for i, stream in enumerate(streams):
        tags = stream.get("tags") or {}
        tracks.append(
            Track(
                index=i,
                title=str(tags.get("title", "") or ""),
                language=str(tags.get("language", "") or ""),
                channels=int(stream.get("channels") or 0),
            )
        )
    return tracks


def extract_track(src: Path, track: Track, dst_dir: Path) -> Path:
    """Вытащить дорожку в моно WAV 16 кГц — формат, который ждёт Whisper."""
    dst_dir.mkdir(parents=True, exist_ok=True)
    dst = dst_dir / f"{src.stem}.track{track.number}.wav"
    cmd = [
        _tool("ffmpeg"), "-y", "-loglevel", "error",
        "-i", str(src),
        "-map", f"0:a:{track.index}",
        "-ac", "1", "-ar", "16000",
        "-c:a", "pcm_s16le",
        str(dst),
    ]
    subprocess.run(cmd, check=True, capture_output=True)
    return dst


def duration_seconds(src: Path) -> float:
    cmd = [
        _tool("ffprobe"), "-v", "error", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1", str(src),
    ]
    out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout.strip()
    try:
        return float(out)
    except ValueError:
        return 0.0


def is_silent(wav: Path, threshold_db: float = -50.0) -> bool:
    """Пустая ли дорожка: OBS иногда пишет молчащие дорожки, их незачем распознавать."""
    cmd = [_tool("ffmpeg"), "-i", str(wav), "-af", "volumedetect", "-f", "null", "-"]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    for line in (proc.stderr or "").splitlines():
        if "mean_volume:" in line:
            try:
                return float(line.split("mean_volume:")[1].split("dB")[0].strip()) < threshold_db
            except (IndexError, ValueError):
                return False
    return False
