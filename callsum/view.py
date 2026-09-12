"""Открытие результатов: чем показывать markdown и как показать файл в проводнике."""

from __future__ import annotations

import shlex
import subprocess
from pathlib import Path
from urllib.parse import quote


def build_open_command(app: str, path: Path) -> list[str]:
    """Во что превратить настройку `[view] markdown_app` для запуска.

    Понимает три записи:
    - пусто — системная ассоциация расширения;
    - шаблон с {file} — команда или ссылка вида `obsidian://open?path={file}`;
    - имя или путь программы — она запускается с файлом в аргументах.
    """
    app = (app or "").strip()
    path = Path(path)
    if not app:
        # Пустой заголовок обязателен: иначе start примет путь в кавычках за заголовок окна.
        return ["cmd", "/c", "start", "", str(path)]
    if "://" in app:
        link = app if "{file}" in app else app.rstrip("/") + "/{file}"
        return ["cmd", "/c", "start", "", link.replace("{file}", quote(str(path), safe=""))]
    if "{file}" in app:
        return [part.replace("{file}", str(path)) for part in _split(app)]
    return [*_split(app), str(path)]


def _split(command: str) -> list[str]:
    r"""Разобрать команду на аргументы, не развалив путь с пробелами.

    Путь вида C:\Program Files\Typora\Typora.exe пробелами не разделяется,
    даже если он записан без кавычек, — иначе запускать было бы нечего.
    """
    if Path(command).is_file():
        return [command]
    return [part.strip('"') for part in shlex.split(command, posix=False)]


def open_document(path: Path, app: str = "") -> None:
    """Открыть файл выбранной программой (или системной ассоциацией)."""
    subprocess.Popen(build_open_command(app, path))


def reveal(path: Path) -> None:
    """Показать файл или папку в проводнике."""
    path = Path(path)
    if path.is_dir():
        subprocess.Popen(["explorer", str(path)])
    else:
        subprocess.Popen(["explorer", "/select,", str(path)])
