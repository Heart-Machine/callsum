"""FFmpeg: программа доносит его сама, если в системе его нет.

Дорожки из записи извлекает ffmpeg, длительность и список дорожек читает
ffprobe — без них не обработать ничего. В установщик они не едут: вдвоём это
двести мегабайт, а установщик и так везёт приложение с ядром. Поэтому здесь
то же, что и с библиотеками CUDA: скачивается при первой обработке, один раз
на машину, и это видно в окне.

Если FFmpeg уже стоит в системе — ничего не скачивается: своя копия нужна
только там, где её нет.

Берётся сборка «essentials» с gyan.dev — та же, что советует официальный сайт
FFmpeg для Windows. Контрольная сумма лежит там же, рядом с архивом, и
скачанное по ней проверяется.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable

from . import config

RELEASE_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
VERSION_URL = "https://www.gyan.dev/ffmpeg/builds/release-version"

# Нужны ровно две программы из архива; остальное (ffplay и документация) —
# лишние сто мегабайт на диске.
TOOLS = ("ffmpeg", "ffprobe")

# Что скачано и какой версии. По набору файлов этого не понять, а знать полезно:
# версия видна на вкладке «О программе».
MARKER = "installed.json"

Progress = Callable[[float | None, str], None]
Log = Callable[[str], None]


class FFmpegError(RuntimeError):
    """Скачать не вышло — с объяснением, что делать."""


def target_dir() -> Path:
    return config.local_dir() / "ffmpeg"


def found(name: str, folder: Path | None = None) -> str | None:
    """Путь к программе: сначала своя копия, потом установленная в системе.

    Своя важнее: человек мог поставить FFmpeg после нас, и тогда две копии
    жили бы рядом — пусть работа идёт той, которую мы проверяли.
    """
    ours = (folder or target_dir()) / f"{name}.exe"
    if ours.is_file():
        return str(ours)
    return shutil.which(name)


def missing(folder: Path | None = None) -> list[str]:
    """Каких программ не хватает."""
    return [name for name in TOOLS if found(name, folder) is None]


def installed(folder: Path | None = None) -> str:
    """Версия скачанной сборки или пустая строка, если своей копии нет."""
    marker = (folder or target_dir()) / MARKER
    try:
        data = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return ""
    return str(data.get("version", "")) if isinstance(data, dict) else ""


def ensure(
    folder: Path | None = None,
    on_progress: Progress | None = None,
    log: Log | None = None,
) -> Path | None:
    """Донести FFmpeg, если его негде взять. Возвращает папку или None.

    None означает «качать не пришлось»: FFmpeg уже есть — наш или системный.
    """
    folder = folder or target_dir()
    if not missing(folder):
        return None

    say = log or (lambda _: None)
    report = on_progress or (lambda _fraction, _detail: None)

    version = _version()
    say(f"Скачиваю FFmpeg {version} — это бывает один раз на машину, около 110 МБ.")
    folder.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="callsum-ffmpeg-") as temporary:
        archive = Path(temporary) / "ffmpeg.zip"
        _download(RELEASE_URL, archive, _digest(), report)
        _extract(archive, folder)

    left = missing(folder)
    if left:
        raise FFmpegError(
            f"В скачанном архиве не нашлось {', '.join(left)}. "
            "Поставьте FFmpeg вручную: winget install Gyan.FFmpeg"
        )

    (folder / MARKER).write_text(
        json.dumps({"version": version}, ensure_ascii=False, indent=2), encoding="utf-8")
    say("FFmpeg на месте")
    report(1.0, "готово")
    return folder


def _version() -> str:
    """Какая версия лежит по общему адресу — её и запишем в пометку."""
    try:
        return _read(VERSION_URL).strip() or "неизвестной версии"
    except FFmpegError:
        # Без версии можно и обойтись: она нужна только для пометки.
        return "неизвестной версии"


def _digest() -> str:
    """Контрольная сумма архива — лежит рядом с ним."""
    try:
        return _read(RELEASE_URL + ".sha256").strip().split()[0].lower()
    except (FFmpegError, IndexError):
        # Без суммы скачаем как есть: битый архив не распакуется, и это
        # заметно сразу, а отказываться от работы из-за неё — перебор.
        return ""


def _read(address: str) -> str:
    try:
        with urllib.request.urlopen(address, timeout=30) as response:
            return response.read().decode("utf-8", "replace")
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise FFmpegError(
            f"Не удалось связаться с сайтом FFmpeg: {exc}. "
            "Проверьте связь с интернетом или поставьте FFmpeg сами: "
            "winget install Gyan.FFmpeg"
        ) from exc


def _download(url: str, destination: Path, digest: str, report: Progress) -> None:
    """Скачать архив, показывая ход, и проверить контрольную сумму."""
    checksum = hashlib.sha256()
    try:
        with urllib.request.urlopen(url, timeout=60) as response:
            size = int(response.headers.get("Content-Length") or 0)
            done = 0
            with destination.open("wb") as target:
                while chunk := response.read(1 << 20):
                    target.write(chunk)
                    checksum.update(chunk)
                    done += len(chunk)
                    report(done / size if size else None, f"{done / 1e6:.0f} МБ")
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise FFmpegError(
            f"Не удалось скачать FFmpeg: {exc}. Проверьте связь с интернетом "
            "или поставьте его сами: winget install Gyan.FFmpeg"
        ) from exc

    if digest and checksum.hexdigest() != digest:
        raise FFmpegError(
            "Скачанный архив FFmpeg повреждён (не сошлась контрольная сумма). "
            "Попробуйте ещё раз."
        )


def _extract(archive: Path, folder: Path) -> None:
    """Достать из архива только ffmpeg и ffprobe."""
    wanted = {f"{name}.exe" for name in TOOLS}
    try:
        with zipfile.ZipFile(archive) as package:
            for entry in package.namelist():
                name = Path(entry).name.lower()
                if name not in wanted:
                    continue
                with package.open(entry) as source, (folder / name).open("wb") as target:
                    shutil.copyfileobj(source, target)
    except (zipfile.BadZipFile, OSError) as exc:
        raise FFmpegError(f"Не удалось распаковать FFmpeg: {exc}") from exc
