"""Загрузка конфигурации из config.toml с дефолтами."""

from __future__ import annotations

import os
import shutil
import sys
import tomllib
from pathlib import Path
from typing import Any

def _frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def _repo_dir() -> Path:
    """Корень исходников — он же рабочая папка разработчика."""
    return Path(__file__).resolve().parent.parent


def config_dir() -> Path:
    """Где живёт config.toml.

    У установленного приложения — в профиле пользователя: программа
    обновляется целиком, и настройки не должны переустанавливаться вместе с ней.
    Раньше файл лежал рядом с ядром, внутри папки сборки, и пересборка его
    стирала вместе со всем, что там накопилось.

    При запуске из исходников ничего не меняется: настройки берутся из
    репозитория, чтобы разработка не перемешивалась с рабочими настройками.
    """
    if not _frozen():
        return _repo_dir()
    appdata = os.environ.get("APPDATA")
    return Path(appdata) / "callsum" if appdata else Path(sys.executable).resolve().parent


def data_dir() -> Path:
    """Папка по умолчанию для записей и результатов.

    Профиль пользователя, а не «Документы»: созвоны весят гигабайтами, а
    «Документы» на многих машинах перенаправлены в OneDrive — записи разговоров
    молча уезжали бы в облако, чего программа обещает не делать.
    """
    return _repo_dir() if not _frozen() else Path.home() / "callsum"


def local_dir() -> Path:
    """Папка для тяжёлого, что программа доносит сама: библиотеки, модели.

    Это не настройки и не данные пользователя: файлы большие, восстановимые
    и переживать обновление обязаны — иначе каждая новая версия тянула бы
    гигабайты заново. Поэтому %LOCALAPPDATA%, а не перемещаемый профиль.

    Имя с «-data» не для красоты: в %LOCALAPPDATA%\\callsum установщик держит
    само приложение и очищает эту папку при установке. Первая же проверка
    установщика унесла оттуда два гигабайта скачанных библиотек.
    """
    local = os.environ.get("LOCALAPPDATA")
    return (Path(local) if local else Path.home() / "AppData" / "Local") / "callsum-data"


# Данные пользователя: recordings, out — относительные пути считаются отсюда.
ROOT = data_dir()
# Ресурсы самой программы: промпты и пример настроек — они внутри сборки.
RESOURCES = Path(__file__).resolve().parent.parent

# В репозитории лежит только пример: config.toml — личный файл пользователя
# (в нём пути, выбранная модель, любимый редактор) и в git не попадает.
EXAMPLE_NAME = "config.example.toml"

DEFAULTS: dict[str, Any] = {
    "paths": {"recordings": "recordings", "out": "out", "folder_template": "{name}"},
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
        "filename_format": "%CCYY-%MM-%DD %hh-%mm-%ss",
        "auto_switch": True,
        "restore_after": True,
        "host": "127.0.0.1",
        "port": 4455,
    },
    "summary": {
        "enabled": True,
        "host": "http://127.0.0.1:11434",
        "model": "qwen3:14b",
        "num_ctx": 8192,
        "temperature": 0.2,
        "think": False,
        # Как долго Ollama держит модель в видеопамяти после ответа.
        "keep_alive": "0s",
        "chunk_chars": 12000,
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
    def __init__(
        self,
        data: dict[str, Any],
        source: Path | None = None,
        created_from: Path | None = None,
    ):
        self.data = data
        self.source = source
        # Из чего файл только что создан — пример или настройки прежней версии.
        # Нужно, чтобы сказать об этом вслух: это разные новости.
        self.created_from = created_from

    @property
    def created(self) -> bool:
        return self.created_from is not None

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


def _inherited_config() -> Path | None:
    """Настройки прежних версий — они лежали рядом с ядром, в папке сборки."""
    if not _frozen():
        return None
    previous = Path(sys.executable).resolve().parent / "config.toml"
    return previous if previous.is_file() else None


def ensure_config(cfg_path: Path) -> Path | None:
    """Создать config.toml, если его ещё нет.

    Настройки прежней версии, лежавшие рядом с ядром, переносятся: пути к папкам
    и выбранную модель пользователь задавал сам, и терять их при переезде нельзя.
    Иначе файл создаётся из примера — править файл с комментариями удобнее, чем
    искать параметры в коде (без него программа тоже работает: всё есть
    в DEFAULTS).

    Возвращает файл, из которого настройки взяты, или None, если создавать
    ничего не пришлось. Откуда именно они взялись, программа говорит вслух:
    «перенёс ваши прежние» и «создал из примера» — разные новости.
    """
    if cfg_path.exists():
        return None

    source = _inherited_config()
    if source is None:
        source = cfg_path.parent / EXAMPLE_NAME
        if not source.is_file():
            source = RESOURCES / EXAMPLE_NAME
    if not source.is_file():
        return None

    cfg_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, cfg_path)
    if source.name == EXAMPLE_NAME:
        _write_absolute_paths(cfg_path)
    return source


def _write_absolute_paths(cfg_path: Path) -> None:
    """Записать в новый файл полные пути к папкам.

    В примере пути относительные — иначе он был бы привязан к одной машине.
    Но в личном файле относительный путь только сбивает с толку: «out» ничего
    не говорит о том, где искать протоколы, и зависит от того, откуда запущена
    программа. Поэтому при создании они разворачиваются в полные.
    """
    from . import settings  # локально: settings импортирует config

    try:
        text = cfg_path.read_text(encoding="utf-8")
        cfg_path.write_text(
            settings.apply(text, {"paths": {
                key: str(data_dir() / DEFAULTS["paths"][key]) for key in ("recordings", "out")
            }}),
            encoding="utf-8",
        )
    except OSError:
        # Не вышло — останутся относительные пути: программа и с ними работает.
        pass


def config_path() -> Path:
    """Полный путь к файлу настроек."""
    return config_dir() / "config.toml"


def load(path: str | Path | None = None) -> Config:
    cfg_path = Path(path) if path else config_path()
    # Свой путь пользователь указал сам: создавать что-то за него не нужно.
    created_from = ensure_config(cfg_path) if path is None else None
    if cfg_path.exists():
        try:
            with cfg_path.open("rb") as fh:
                user = tomllib.load(fh)
        except tomllib.TOMLDecodeError as exc:
            raise ConfigError(
                f"Не удалось прочитать {cfg_path}:\n{exc}\n\n"
                "Проверьте синтаксис TOML. Если это путь Windows в двойных кавычках, "
                "обратные слеши в нём нужно удваивать; проще записать путь в одинарных кавычках: "
                r"'C:\Program Files\Typora\Typora.exe'"
            ) from exc
        data = _merge(DEFAULTS, user)
        _validate(data)
        return Config(data, cfg_path, created_from)
    _validate(DEFAULTS)
    return Config(DEFAULTS, None)


def _validate(data: dict[str, Any]) -> None:
    """Проверить значения, от которых зависят границы циклов обработки."""
    summary = data.get("summary", {})
    for key, minimum in (("chunk_chars", 1), ("chunk_overlap_chars", 0)):
        value = summary.get(key)
        if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
            bound = "положительное" if minimum else "неотрицательное"
            raise ConfigError(
                f"Некорректное значение [summary] {key}: ожидалось {bound} целое число."
            )
