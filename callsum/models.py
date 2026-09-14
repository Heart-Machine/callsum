"""Модель распознавания: скачивается при первом запуске, и это видно.

Веса large-v3 весят около трёх гигабайт. Их тянет faster-whisper, и делает это
молча: окно приложения показывало «Загружаю модель распознавания…» и висело так
десятки минут, а человек не знал, идёт работа или всё встало.

Здесь то же скачивание, но с отчётом о ходе — и с папкой, где модель переживёт
обновление программы.
"""

from __future__ import annotations

import os
import threading
from pathlib import Path
from typing import Callable

from . import config

Progress = Callable[[float | None, str], None]
Log = Callable[[str], None]


class ModelError(RuntimeError):
    """Модель не скачалась — с объяснением, что делать."""


def model_dir(cfg) -> Path | None:
    """Где держать веса.

    Настройка важнее всего: человек мог увести модели на другой диск. Иначе
    у установленного приложения — папка рядом с остальным скачанным, чтобы
    обновление не потянуло три гигабайта заново. При запуске из исходников
    остаётся кэш Hugging Face: он общий с другими проектами, и второй копии
    весов на диске разработчика не нужно.
    """
    chosen = str(cfg.transcribe.get("model_dir", "") or "").strip()
    if chosen:
        return Path(chosen)
    return config.local_dir() / "models" if config._frozen() else None


class _Counter:
    """Сколько уже скачано по всем файлам сразу.

    Hugging Face качает файлы параллельно и заводит полосу на каждый. Показывать
    их по очереди бессмысленно: на фоне трёхгигабайтных весов остальные файлы —
    это доли процента, и полоса дёргалась бы туда-сюда.
    """

    def __init__(self, report: Progress):
        self._report = report
        self._bars: dict[int, tuple[int, int]] = {}
        self._lock = threading.Lock()

    def note(self, key: int, done: int, total: int) -> None:
        with self._lock:
            self._bars[key] = (done, total)
            done_all = sum(value for value, _ in self._bars.values())
            total_all = sum(value for _, value in self._bars.values())

        self._report(
            done_all / total_all if total_all else None,
            f"{done_all / 1e6:.0f} из {total_all / 1e6:.0f} МБ" if total_all else "",
        )


def _watcher(counter: _Counter):
    """Полоса прогресса в том виде, в каком её ждёт huggingface_hub."""
    from tqdm.auto import tqdm

    class Watcher(tqdm):
        def __init__(self, *args, **kwargs):
            # Считаем сами: выключенная полоса tqdm не ведёт учёт вовсе —
            # `update` у неё выходит сразу, не трогая счётчик.
            self.measure = kwargs.get("unit", "")
            # Сколько всего — узнаётся не сразу: huggingface_hub заводит общую
            # полосу с нулём, а размер проставляет, когда узнает его от сервера.
            # Поэтому total читается при каждом обновлении, а не запоминается.
            self.title = str(kwargs.get("desc") or "")
            self.done = 0
            # Рисовать в консоль нечего: ядро работает без неё, а ход работы
            # уходит событиями в окно.
            kwargs["disable"] = True
            super().__init__(*args, **kwargs)

        def update(self, n=1):
            result = super().update(n)
            self.done += n or 0
            if self._counts_payload():
                counter.note(id(self), self.done, self.total)
            return result

        def _counts_payload(self) -> bool:
            """Считает ли эта полоса то, чего ждёт человек.

            Кроме скачанных байтов, huggingface_hub ведёт полосу «сколько файлов
            осталось» и вторую, байтовую, — о записи на диск. Первая мешает
            складывать, вторая удваивает объём: это те же байты, только с другой
            стороны. Человеку интересны скачанные.
            """
            return (
                self.measure == "B"
                and bool(self.total)
                and not self.title.startswith("Reconstructing")
            )

    return Watcher


def ensure(cfg, log: Log | None = None, on_progress: Progress | None = None) -> str | None:
    """Убедиться, что модель на месте. Возвращает путь к ней или None.

    None означает «не вышло, действуйте как раньше»: faster-whisper попробует
    скачать её сам, а если сети нет — возьмёт из кэша.
    """
    say = log or (lambda _: None)
    name = str(cfg.transcribe["model"])
    folder = model_dir(cfg)
    cache = str(folder) if folder else None

    _no_symlinks()
    faster_whisper_utils = _import_utils()

    cached = _cached(faster_whisper_utils, name, cache)
    if cached is not None and _ready(cached, say):
        return cached

    say(f"Скачиваю модель распознавания {name} — это бывает один раз на машине.")
    try:
        weights = _download(faster_whisper_utils, name, cache, on_progress)
        if on_progress is not None:
            # Последние байты полоса не всегда успевает показать, и она замирала
            # на 99%. Закрываем её сами — работа-то закончена.
            on_progress(1.0, "готово")
        return weights
    except Exception as exc:  # noqa: BLE001 — причина уходит человеку в окно
        say(
            f"Не удалось скачать модель {name}: {exc}. "
            "Проверьте связь с интернетом и попробуйте ещё раз."
        )
        return None


def _no_symlinks() -> None:
    """Запретить Hugging Face складывать кэш из символических ссылок.

    По умолчанию он кладёт файлы в blobs, а в снимок модели ставит ссылки на
    них. Windows позволяет такие ссылки создать, но не всегда позволяет по ним
    ходить: в %LOCALAPPDATA% на проверочной машине ссылка создаётся, а открыть
    её нельзя — «не найден путь». Проверка самого Hugging Face этого не ловит:
    она только пробует ссылку создать. В итоге три скачанных гигабайта
    оказывались нечитаемыми, а распознавание падало на «Unable to open file
    model.bin».

    Без ссылок он кладёт скачанное сразу на место — лишнего места это не
    занимает.
    """
    os.environ["HF_HUB_DISABLE_SYMLINKS"] = "1"
    try:
        from huggingface_hub import constants
    except ImportError:
        # Настройка через переменную окружения сработает при следующем запуске.
        return

    # Переменную окружения он читает один раз, при импорте, а импортируют его
    # задолго до первого скачивания — поэтому правим и то, что уже прочитано.
    constants.HF_HUB_DISABLE_SYMLINKS = True


def _ready(folder: str, say: Log) -> bool:
    """Годится ли то, что нашлось в кэше, или его надо чинить."""
    broken = _broken(Path(folder))
    if not broken:
        return True

    say("Модель на месте, но кэш собран из ссылок, по которым Windows не ходит. Чиню…")
    return _repair(broken, say)


def _broken(folder: Path) -> list[Path]:
    """Ссылки, по которым нельзя пройти, — при живом содержимом."""
    try:
        entries = list(folder.iterdir())
    except OSError:
        # Папки нет или её не прочитать — чинить нечего, пусть решает вызывающий.
        return []

    broken = []
    for entry in entries:
        if not entry.is_symlink():
            continue
        try:
            entry.stat()
        except OSError:
            broken.append(entry)
    return broken


def _repair(broken: list[Path], say: Log) -> bool:
    """Подставить содержимое на место ссылки.

    Содержимое никуда не делось — оно лежит в blobs, — поэтому качать заново
    не нужно: файл просто переставляется туда, куда указывала ссылка.
    """
    for link in broken:
        target = Path(os.path.normpath(link.parent / os.readlink(link)))
        if not target.exists():
            say(f"Не нашёл содержимое для {link.name} — скачаю модель заново.")
            return False
        try:
            link.unlink()
            os.replace(target, link)
        except OSError as exc:
            say(f"Не вышло починить {link.name}: {exc}. Скачаю модель заново.")
            return False

    say("Кэш модели починен, скачивать заново не нужно")
    return True


def _import_utils():
    """Отдельной функцией — чтобы тесты обходились без faster-whisper и сети."""
    from faster_whisper import utils

    return utils


def _cached(utils, name: str, cache: str | None) -> str | None:
    """Путь к уже скачанной модели или None, если её нет."""
    try:
        return utils.download_model(name, cache_dir=cache, local_files_only=True)
    except Exception:  # noqa: BLE001 — «нет в кэше» приходит разными исключениями
        return None


def _download(utils, name: str, cache: str | None, on_progress: Progress | None) -> str:
    """Скачать модель, подменив немую полосу прогресса на говорящую.

    faster-whisper жёстко отключает вывод хода работы: в `download_model`
    подставлен `disabled_tqdm`. Своего способа узнать о ходе он не даёт,
    поэтому на время скачивания подменяется именно этот класс — так не приходится
    повторять у себя ни таблицу имён моделей, ни список нужных файлов.
    """
    previous = getattr(utils, "disabled_tqdm", None)
    if on_progress is None or previous is None:
        return utils.download_model(name, cache_dir=cache)

    utils.disabled_tqdm = _watcher(_Counter(on_progress))
    try:
        return utils.download_model(name, cache_dir=cache)
    finally:
        utils.disabled_tqdm = previous
