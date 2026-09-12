"""Загрузка конфигурации из config.toml с дефолтами."""

from __future__ import annotations

import tomllib
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent

DEFAULTS: dict[str, Any] = {
    "paths": {"recordings": "recordings", "out": "out"},
    "audio": {
        "extensions": [".mkv", ".mp4", ".mka", ".m4a", ".mp3", ".wav", ".flac", ".opus", ".webm"],
        "stable_seconds": 20,
    },
    "transcribe": {
        "model": "large-v3",
        "device": "auto",
        "compute_type": "auto",
        "language": "ru",
        "beam_size": 5,
        "vad": True,
        "split_gap": 1.5,
        "model_dir": "",
    },
    "speakers": {"1": "Я", "2": "Собеседник", "default": "Говорящий", "merge_gap": 2.0},
    "view": {"markdown_app": ""},
    "obs": {
        "profile": "callsum",
        "auto_switch": True,
        "restore_after": True,
        "host": "127.0.0.1",
        "port": 0,
    },
    "summary": {
        "enabled": True,
        "host": "http://127.0.0.1:11434",
        "model": "qwen3:14b",
        "num_ctx": 16384,
        "temperature": 0.2,
        "think": False,
        "chunk_chars": 24000,
        "chunk_overlap_chars": 1500,
        "timeout_seconds": 1800,
    },
}


def _merge(base: dict, over: dict) -> dict:
    out = dict(base)
    for key, value in over.items():
        if isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = _merge(out[key], value)
        else:
            out[key] = value
    return out


class Config:
    def __init__(self, data: dict[str, Any], source: Path | None = None):
        self.data = data
        self.source = source

    @property
    def paths(self) -> dict: return self.data["paths"]

    @property
    def audio(self) -> dict: return self.data["audio"]

    @property
    def transcribe(self) -> dict: return self.data["transcribe"]

    @property
    def speakers(self) -> dict: return self.data["speakers"]

    @property
    def view(self) -> dict: return self.data["view"]

    @property
    def obs(self) -> dict: return self.data["obs"]

    @property
    def summary(self) -> dict: return self.data["summary"]

    def path(self, key: str) -> Path:
        """Путь из секции [paths], приведённый к абсолютному."""
        raw = Path(self.paths[key])
        return raw if raw.is_absolute() else (ROOT / raw)

    def speaker_for_track(self, track_no: int) -> str:
        """Имя говорящего для дорожки (нумерация с 1)."""
        return str(self.speakers.get(str(track_no), f"Дорожка {track_no}"))


def load(path: str | Path | None = None) -> Config:
    cfg_path = Path(path) if path else (ROOT / "config.toml")
    if cfg_path.exists():
        with cfg_path.open("rb") as fh:
            user = tomllib.load(fh)
        return Config(_merge(DEFAULTS, user), cfg_path)
    return Config(DEFAULTS, None)
