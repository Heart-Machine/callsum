"""Библиотеки CUDA: приложение доносит их само, а не везёт в установщике.

Вместе cuBLAS, cuDNN и nvrtc весят около двух гигабайт — больше, чем всё
остальное приложение, и больше, чем можно положить одним файлом в релиз
GitHub. Поэтому установщик остаётся лёгким, а библиотеки скачиваются при первом
запуске распознавания — один раз на машину.

Берутся они с PyPI, из тех же официальных пакетов `nvidia-*-cu12`, которые
ставит `requirements.txt`: своего зеркала у нас нет, а у этих колёс есть
контрольная сумма, по которой скачанное проверяется.
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

# Версии те же, что стоят в рабочем окружении: на них всё проверено.
PACKAGES: tuple[tuple[str, str], ...] = (
    ("nvidia-cublas-cu12", "12.9.2.10"),
    ("nvidia-cudnn-cu12", "9.26.0.51"),
    ("nvidia-cuda-nvrtc-cu12", "12.9.86"),
)

# Что уже скачано и каких версий. Без этой пометки пришлось бы гадать
# по набору файлов, а он у разных версий отличается.
MARKER = "installed.json"

Progress = Callable[[float | None, str], None]
Log = Callable[[str], None]


class CudaError(RuntimeError):
    """Библиотеки не удалось донести — с объяснением, что делать."""


def target_dir() -> Path:
    return config.local_dir() / "cuda"


def installed(folder: Path | None = None) -> dict[str, str]:
    """Какие пакеты уже лежат в папке."""
    marker = (folder or target_dir()) / MARKER
    try:
        data = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return {str(k): str(v) for k, v in data.items()} if isinstance(data, dict) else {}


def missing(folder: Path | None = None) -> list[tuple[str, str]]:
    """Пакеты, которых не хватает или которые устарели."""
    have = installed(folder)
    return [(name, version) for name, version in PACKAGES if have.get(name) != version]


def ensure(
    folder: Path | None = None,
    on_progress: Progress | None = None,
    log: Log | None = None,
) -> Path:
    """Донести недостающие библиотеки. Возвращает папку, где они лежат."""
    folder = folder or target_dir()
    needed = missing(folder)
    if not needed:
        return folder

    say = log or (lambda _: None)
    report = on_progress or (lambda _fraction, _detail: None)
    folder.mkdir(parents=True, exist_ok=True)

    total = len(needed)
    say(
        "Скачиваю библиотеки CUDA — это бывает один раз на машину, "
        "около гигабайта."
    )
    for index, (name, version) in enumerate(needed):
        def step(share: float | None, detail: str = name, position: int = index) -> None:
            # Доля считается по всем пакетам сразу: иначе полоса трижды
            # пробежала бы от нуля до конца, и было бы непонятно, сколько ждать.
            report(None if share is None else (position + share) / total, detail)

        _fetch(name, version, folder, step, say)
        _remember(folder, name, version)

    say("Библиотеки CUDA на месте")
    report(1.0, "готово")
    return folder


def _fetch(name: str, version: str, folder: Path, report: Progress, say: Log) -> None:
    url, digest = _wheel(name, version)
    say(f"  {name} {version}…")
    with tempfile.TemporaryDirectory(prefix="callsum-cuda-") as temporary:
        archive = Path(temporary) / f"{name}.whl"
        _download(url, archive, digest, report)
        _extract(archive, folder)


def _read_json(address: str) -> dict:
    try:
        with urllib.request.urlopen(address, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, ValueError, OSError) as exc:
        raise CudaError(
            f"Не удалось узнать, откуда качать библиотеки CUDA: {exc}. "
            "Проверьте связь с интернетом и попробуйте ещё раз."
        ) from exc


def _wheel(name: str, version: str) -> tuple[str, str]:
    """Адрес колеса под Windows и его контрольная сумма — со страницы пакета."""
    data = _read_json(f"https://pypi.org/pypi/{name}/{version}/json")

    for entry in data.get("urls", []):
        filename = str(entry.get("filename", ""))
        if filename.endswith("win_amd64.whl"):
            return str(entry["url"]), str(entry.get("digests", {}).get("sha256", ""))

    raise CudaError(
        f"Для {name} {version} нет сборки под Windows — видимо, версия снята. "
        "Обновите callsum."
    )


def _download(url: str, destination: Path, digest: str, report: Progress) -> None:
    """Скачать файл, показывая ход, и проверить контрольную сумму."""
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
        raise CudaError(
            f"Не удалось скачать библиотеки CUDA: {exc}. "
            "Проверьте связь с интернетом и попробуйте ещё раз."
        ) from exc

    if digest and checksum.hexdigest() != digest:
        # Битый файл хуже отсутствующего: библиотека не загрузится, а искать
        # причину придётся в падении CTranslate2 где-то в недрах.
        raise CudaError(
            "Скачанный файл библиотек повреждён (не сошлась контрольная сумма). "
            "Попробуйте ещё раз."
        )


def _extract(archive: Path, folder: Path) -> None:
    """Выложить из колеса только библиотеки — рядом с ядром их и ищет Windows."""
    try:
        with zipfile.ZipFile(archive) as wheel:
            for entry in wheel.namelist():
                if not entry.lower().endswith(".dll") or "/bin/" not in entry.replace("\\", "/"):
                    continue
                with wheel.open(entry) as source, (folder / Path(entry).name).open("wb") as target:
                    shutil.copyfileobj(source, target)
    except (zipfile.BadZipFile, OSError) as exc:
        raise CudaError(f"Не удалось распаковать библиотеки CUDA: {exc}") from exc


def _remember(folder: Path, name: str, version: str) -> None:
    have = installed(folder)
    have[name] = version
    (folder / MARKER).write_text(
        json.dumps(have, ensure_ascii=False, indent=2), encoding="utf-8")
