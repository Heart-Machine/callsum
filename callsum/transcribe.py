"""Распознавание речи через faster-whisper (CTranslate2)."""

from __future__ import annotations

import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path

from . import models


def _register_cuda_dlls() -> None:
    """Показать Windows, где лежат cuDNN/cuBLAS из пакетов nvidia-*-cu12.

    Без этого CTranslate2 не находит cudnn64_9.dll и падает при device="cuda".
    """
    if os.name != "nt":
        return
    if getattr(sys, "frozen", False):
        from . import cuda

        # В собранном ядре библиотеки лежат либо рядом с исполняемым файлом
        # (старые сборки везли их с собой), либо в папке, куда программа
        # скачала их при первом запуске.
        for folder in (
            cuda.target_dir(),
            Path(sys.executable).parent,
            Path(__file__).resolve().parent.parent,
        ):
            try:
                os.add_dll_directory(str(folder))
            except OSError:
                pass
            # Одного add_dll_directory мало: CTranslate2 грузит cublas64_12.dll
            # сам, обычным LoadLibrary, а тот смотрит в PATH и не знает про
            # добавленные каталоги. Пока библиотеки лежали рядом с ядром, это
            # было незаметно — папку программы Windows ищет всегда.
            if folder.is_dir():
                os.environ["PATH"] = str(folder) + os.pathsep + os.environ.get("PATH", "")
        return
    for site in sys.path:
        nvidia = Path(site) / "nvidia"
        if not nvidia.is_dir():
            continue
        for dll_dir in nvidia.glob("*/bin"):
            try:
                os.add_dll_directory(str(dll_dir))
            except OSError:
                pass
            os.environ["PATH"] = str(dll_dir) + os.pathsep + os.environ.get("PATH", "")


def _stage(on_progress, stage: str):
    """Переложить отчёт о ходе работы на нужную стадию.

    Скачиваний два — библиотеки и веса модели, — и в окне это разные надписи.
    """
    if on_progress is None:
        return None
    return lambda fraction, detail: on_progress(stage, fraction, detail)


def _ensure_cuda(log, on_progress=None) -> None:
    """Донести библиотеки CUDA, если их ещё нет.

    Только в собранном приложении: при запуске из исходников они приезжают
    вместе с пакетами `nvidia-*-cu12` из requirements.txt.
    """
    if not getattr(sys, "frozen", False):
        return

    from . import cuda

    try:
        cuda.ensure(on_progress=on_progress, log=log)
    except cuda.CudaError as exc:
        # Без библиотек остаётся процессор: медленно, но работает, и это
        # честнее, чем падение посреди созвона.
        log(f"{exc} Пока распознаю на процессоре.")


@dataclass
class Segment:
    start: float
    end: float
    text: str
    speaker: str


class Transcriber:
    """Обёртка над моделью Whisper: грузится один раз, работает по всем дорожкам."""

    def __init__(self, cfg, verbose: bool = True, on_progress=None):
        self.cfg = cfg
        self.verbose = verbose
        tr = cfg.transcribe
        device, compute = self._resolve_device(tr["device"], tr["compute_type"])
        if device == "cuda":
            # Установщик лёгкий: библиотеки CUDA программа доносит сама, при
            # первом распознавании. Дальше они лежат и переживают обновления.
            _ensure_cuda(self._log, _stage(on_progress, "download"))

        _register_cuda_dlls()
        from faster_whisper import WhisperModel  # импорт после настройки DLL

        kwargs = {"device": device, "compute_type": compute}
        if tr.get("model_dir"):
            kwargs["download_root"] = tr["model_dir"]

        # Веса скачиваются заранее и с отчётом о ходе: сам faster-whisper делает
        # это молча, и окно висело с надписью «Загружаю модель» неизвестно сколько.
        # Дальше модель грузится уже с диска — по готовому пути, без сети.
        weights = models.ensure(cfg, self._log, _stage(on_progress, "model"))
        name = weights or tr["model"]
        self._log(f"Загружаю модель {tr['model']} ({device}/{compute})…")
        try:
            self.model = self._load(WhisperModel, name, kwargs)
        except Exception as exc:  # noqa: BLE001
            if _looks_like_network(exc):
                # Веса уже могут лежать в кэше — не повод падать из-за сети.
                self._log("Нет связи с Hugging Face, беру модель из локального кэша…")
                try:
                    self.model = self._load(WhisperModel, name, kwargs, local_files_only=True)
                except Exception as cached_exc:  # noqa: BLE001
                    raise RuntimeError(
                        f"Модель {tr['model']} не скачана, а сеть недоступна: {cached_exc}"
                    ) from exc
            elif device == "cuda" and _looks_like_cuda(exc):
                self._log(f"GPU недоступен ({exc}). Перехожу на CPU — будет заметно медленнее.")
                device, compute = "cpu", "int8"
                kwargs.update(device=device, compute_type=compute)
                self.model = self._load(WhisperModel, name, kwargs)
            else:
                raise
        self.device = device
        self.compute_type = compute

    @staticmethod
    def _load(cls, name: str, kwargs: dict, **extra):
        return cls(name, **{**kwargs, **extra})

    @staticmethod
    def _resolve_device(device: str, compute: str) -> tuple[str, str]:
        if device == "auto":
            try:
                import ctranslate2

                device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
            except Exception:  # noqa: BLE001
                device = "cpu"
        if compute == "auto":
            compute = "float16" if device == "cuda" else "int8"
        return device, compute

    def _log(self, msg: str) -> None:
        if self.verbose:
            print(msg, flush=True)

    def release(self) -> None:
        """Убрать веса из видеопамяти, оставив их в обычной.

        Пока модель распознавания занимает свою долю карты, языковой модели
        не хватает места: она начинает возить данные через системную память,
        и протокол готовится в десятки раз дольше. Замер на карте 12 ГБ: один
        и тот же запрос — 4 секунды без модели распознавания и 47 секунд с ней.
        Обратная загрузка идёт из памяти, а не с диска, и занимает меньше секунды.
        """
        model = getattr(self.model, "model", None)
        if model is None or not getattr(model, "model_is_loaded", False):
            return
        model.unload_model(to_cpu=True)

    def _ensure_loaded(self) -> None:
        model = getattr(self.model, "model", None)
        if model is not None and not model.model_is_loaded:
            model.load_model()

    def run(
        self, wav: Path, speaker: str, total_seconds: float = 0.0, on_progress=None
    ) -> list[Segment]:
        self._ensure_loaded()
        tr = self.cfg.transcribe
        segments, info = self.model.transcribe(
            str(wav),
            language=tr["language"] or None,
            beam_size=int(tr["beam_size"]),
            vad_filter=bool(tr["vad"]),
            vad_parameters={"min_silence_duration_ms": 500},
            # Выключено намеренно: с включённым контекстом large-v3 на длинных
            # паузах созвона уходит в повторы одной и той же фразы.
            condition_on_previous_text=False,
            # Нужны не для красоты: с vad_filter начало сегмента растягивается
            # на вырезанную тишину, и реплики двух дорожек перемешиваются при
            # склейке. Границы по словам дают настоящее время начала фразы.
            word_timestamps=True,
        )
        total = total_seconds or getattr(info, "duration", 0.0) or 0.0
        out: list[Segment] = []
        started = time.monotonic()
        last_report = 0.0
        split_gap = float(tr.get("split_gap", 1.5))
        for seg in segments:
            out += _segments_from_words(seg, speaker, split_gap)
            if on_progress is not None and total:
                on_progress(min(seg.end / total, 1.0), speaker)
            if self.verbose and seg.end - last_report >= 30:
                last_report = seg.end
                elapsed = time.monotonic() - started
                pct = f"{seg.end / total * 100:5.1f}%" if total else "  ?  "
                print(
                    f"  [{speaker}] {pct}  {_hhmmss(seg.end)} / {_hhmmss(total)}"
                    f"  (скорость x{seg.end / elapsed:.1f})",
                    flush=True,
                )
        self._log(f"  [{speaker}] готово: {len(out)} реплик за {time.monotonic() - started:.0f} с")
        return out


def _looks_like_network(exc: Exception) -> bool:
    """Похоже ли, что не скачались веса, а не сломалось железо."""
    text = f"{type(exc).__name__}: {exc}".lower()
    markers = (
        "connection", "disconnected", "timeout", "timed out", "network",
        "http", "ssl", "resolve", "temporarily unavailable",
    )
    return any(m in text for m in markers)


def _looks_like_cuda(exc: Exception) -> bool:
    text = f"{type(exc).__name__}: {exc}".lower()
    return any(m in text for m in ("cuda", "cudnn", "cublas", "gpu", "driver"))


def _segments_from_words(seg, speaker: str, split_gap: float) -> list[Segment]:
    """Разбить сегмент Whisper на реплики по словам.

    Даёт две вещи, которых нет в сырых сегментах: честное время начала фразы
    (см. word_timestamps выше) и разрез там, где внутри одного сегмента
    оказалась пауза в несколько секунд — обычно это две разные реплики.
    """
    words = [w for w in (seg.words or []) if w.word.strip()]
    if not words:
        text = seg.text.strip()
        return [Segment(seg.start, seg.end, text, speaker)] if text else []

    out: list[Segment] = []
    chunk = [words[0]]
    for prev, word in zip(words, words[1:]):
        if word.start - prev.end > split_gap:
            out.append(_from_chunk(chunk, speaker))
            chunk = []
        chunk.append(word)
    if chunk:
        out.append(_from_chunk(chunk, speaker))
    return [s for s in out if s.text]


def _from_chunk(words, speaker: str) -> Segment:
    return Segment(
        start=words[0].start,
        end=words[-1].end,
        text="".join(w.word for w in words).strip(),
        speaker=speaker,
    )


def _hhmmss(seconds: float) -> str:
    seconds = int(seconds)
    return f"{seconds // 3600:02d}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


hhmmss = _hhmmss
