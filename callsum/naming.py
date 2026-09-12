"""Имена папок с результатами: шаблон из настроек и приведение к виду, годному для Windows."""

from __future__ import annotations

from datetime import datetime
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
