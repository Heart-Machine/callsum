"""Имена папок с результатами: шаблон из настроек и приведение к виду, годному для Windows."""

from __future__ import annotations

from datetime import datetime
import json
import os
from pathlib import Path

DEFAULT_TEMPLATE = "{name}"

# Символы, которых не бывает в именах файлов Windows.
FORBIDDEN = '<>:"/\\|?*'

PLACEHOLDERS = {
    "{name}": "имя файла записи без расширения",
    "{date}": "дата записи, ГГГГ-ММ-ДД",
    "{time}": "время записи, ЧЧ-ММ-СС",
    "{datetime}": "дата и время записи",
}


def _timestamp(src: Path) -> datetime:
    try:
        return datetime.fromtimestamp(src.stat().st_mtime)
    except OSError:
        # Файла может не быть (например, имя считают заранее) — тогда «сейчас».
        return datetime.now()


def sanitize(name: str) -> str:
    """Убрать из имени всё, на чём Windows откажется создавать папку."""
    for char in FORBIDDEN:
        name = name.replace(char, "-")
    # Точка и пробел в конце имени папки Windows не сохраняет.
    return " ".join(name.split()).rstrip(". ")


def folder_name(src: Path, template: str = DEFAULT_TEMPLATE, when: datetime | None = None) -> str:
    """Имя папки с результатами по шаблону из config.toml.

    Неизвестные подстановки оставляем как есть, а не роняем обработку:
    из-за опечатки в шаблоне не должен пропадать уже записанный созвон.
    """
    src = Path(src)
    stamp = when or _timestamp(src)
    values = {
        "name": src.stem,
        "date": stamp.strftime("%Y-%m-%d"),
        "time": stamp.strftime("%H-%M-%S"),
        "datetime": stamp.strftime("%Y-%m-%d %H-%M-%S"),
    }
    rendered = template or DEFAULT_TEMPLATE
    for key, value in values.items():
        rendered = rendered.replace("{" + key + "}", str(value))
    return sanitize(rendered) or sanitize(src.stem) or "созвон"


def result_dir(src: Path, out_root: Path, template: str = DEFAULT_TEMPLATE) -> Path:
    """Папка результата, принадлежащая именно этой исходной записи.

    Имя по шаблону остаётся первым выбором ради уже созданных результатов. Если
    оно занято другой записью, её путь в transcript.json позволяет выбрать
    стабильный соседний каталог, не перезаписывая чужой созвон.
    """
    src = Path(src).resolve()
    root = Path(out_root)
    base = folder_name(src, template)
    candidate = root / base
    index = 2
    while _belongs_to_another_source(candidate, src):
        candidate = root / f"{base} ({index})"
        index += 1
    return candidate


def _belongs_to_another_source(folder: Path, src: Path) -> bool:
    metadata = folder / "transcript.json"
    try:
        source = json.loads(metadata.read_text(encoding="utf-8")).get("source")
    except (OSError, ValueError, AttributeError):
        return False
    if not isinstance(source, str) or not source:
        return False
    return _path_key(source) != _path_key(src)


def _path_key(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(str(path)))
