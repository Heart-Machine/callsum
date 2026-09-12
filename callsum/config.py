"""Загрузка конфигурации из config.toml с дефолтами."""

from __future__ import annotations

import shutil
import tomllib
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent

# В репозитории лежит только пример: config.toml — личный файл пользователя
# (в нём пути, выбранная модель, любимый редактор) и в git не попадает.
EXAMPLE_NAME = "config.example.toml"

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


class ConfigError(RuntimeError):
    """Конфиг есть, но его не удалось прочитать."""


class Config:
    def __init__(self, data: dict[str, Any], source: Path | None = None, created: bool = False):
        self.data = data
        self.source = source
        # Правда ли, что файл только что создан из примера — чтобы сказать об этом.
        self.created = created

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


def ensure_config(cfg_path: Path) -> bool:
    """Создать config.toml из примера, если его ещё нет.

    Возвращает True, если файл был создан. Без него программа тоже работает —
    все значения продублированы в DEFAULTS, — но править удобнее файл
    с комментариями, чем искать параметры в коде.
    """
    example = cfg_path.parent / EXAMPLE_NAME
    if cfg_path.exists() or not example.is_file():
        return False
    shutil.copyfile(example, cfg_path)
    return True


def load(path: str | Path | None = None) -> Config:
    cfg_path = Path(path) if path else (ROOT / "config.toml")
    # Свой путь пользователь указал сам: создавать что-то за него не нужно.
    created = ensure_config(cfg_path) if path is None else False
    if cfg_path.exists():
        try:
            with cfg_path.open("rb") as fh:
                user = tomllib.load(fh)
        except tomllib.TOMLDecodeError as exc:
            raise ConfigError(
                f"Не удалось прочитать {cfg_path}:\n{exc}\n\n"
                "Частая причина — путь Windows в двойных кавычках: обратный слеш там "
                "нужно удваивать. Проще записать путь в одинарных кавычках: "
                r"'C:\Program Files\Typora\Typora.exe'"
            ) from exc
        return Config(_merge(DEFAULTS, user), cfg_path, created)
    return Config(DEFAULTS, None)
