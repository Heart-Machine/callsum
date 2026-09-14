"""Работа с аудио: разбор дорожек и извлечение их в 16 кГц моно WAV."""

from __future__ import annotations

import json
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from . import ffmpeg as ffmpeg_tools

# Сколько ждать признаков работы, прежде чем считать ffmpeg зависшим.
# Извлечение дорожки идёт примерно в две тысячи раз быстрее реального времени
# (двадцать минут записи — половина секунды), так что три минуты молчания —
# это не медленная работа, а остановка.
STALL_SECONDS = 180.0

# Разбор заголовков файла — чтение нескольких килобайт; ждать дольше незачем.
PROBE_TIMEOUT = 60.0


class FFmpegMissing(RuntimeError):
    pass


class FFmpegStalled(RuntimeError):
    """ffmpeg перестал подавать признаки работы — он встал, а не считает долго."""


class FFmpegFailed(RuntimeError):
    """ffmpeg отказался работать; в сообщении — его собственное объяснение."""


def _run_ffmpeg(cmd: list[str], *, what: str, stall_seconds: float = STALL_SECONDS) -> str:
    """Запустить ffmpeg под присмотром и вернуть то, что он написал в поток ошибок.

    Сторож следит не за общим временем работы, а за движением: по ключу
    `-progress` ffmpeg несколько раз в секунду отчитывается о ходе дела, и пока
    отчёты идут, работа считается живой, сколько бы она ни длилась. Молчание
    дольше `stall_seconds` означает, что процесс встал.

    Разница не умозрительная: однажды ffmpeg дописал 38 МБ из 38 и замер на
    двенадцать минут, не тратя ни процессора, ни диска. Обработка не двигалась,
    а окно показывало, что всё идёт своим чередом.

    `-nostdin` обязателен: ядро запускается приложением, и его стандартный ввод —
    труба с командами. Без запрета ffmpeg вправе читать оттуда свои горячие
    клавиши и съесть команду, адресованную ядру.
    """
    return _supervise(_watched(cmd), what=what, stall_seconds=stall_seconds)


def _watched(cmd: list[str]) -> list[str]:
    """Добавить ключи, которые делают работу ffmpeg видимой сторожу."""
    return [cmd[0], "-nostdin", "-progress", "pipe:1", "-nostats", *cmd[1:]]


def _supervise(command: list[str], *, what: str, stall_seconds: float) -> str:
    """Выполнить команду, следя за тем, что она подаёт признаки жизни."""
    proc = subprocess.Popen(
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    last_sign = time.monotonic()
    errors: list[bytes] = []
    lock = threading.Lock()

    def follow(stream, keep: bool) -> None:
        nonlocal last_sign
        for line in stream:
            with lock:
                last_sign = time.monotonic()
                if keep:
                    errors.append(line)
        stream.close()

    watchers = [
        threading.Thread(target=follow, args=(proc.stdout, False), daemon=True),
        threading.Thread(target=follow, args=(proc.stderr, True), daemon=True),
    ]
    for watcher in watchers:
        watcher.start()

    while proc.poll() is None:
        with lock:
            silent_for = time.monotonic() - last_sign
        if silent_for > stall_seconds:
            proc.kill()
            proc.wait()
            raise FFmpegStalled(
                f"ffmpeg перестал отвечать: {what}. Признаков работы нет "
                f"{silent_for:.0f} с — похоже, он завис. Запись цела: "
                "попробуйте обработать её ещё раз."
            )
        time.sleep(0.5)

    for watcher in watchers:
        watcher.join(timeout=5)

    text = b"".join(errors).decode("utf-8", "replace")
    if proc.returncode != 0:
        raise FFmpegFailed(f"ffmpeg не смог {what}: {_last_words(text)}")
    return text


def _last_words(text: str, lines: int = 3) -> str:
    """Последние строки жалоб ffmpeg: в них суть, остальное — шум."""
    meaningful = [line.strip() for line in text.splitlines() if line.strip()]
    return " / ".join(meaningful[-lines:]) or "он не объяснил причину"


def _run_probe(cmd: list[str], *, what: str) -> str:
    """Запустить ffprobe. Он не отчитывается о ходе работы, зато и работает мгновенно."""
    try:
        done = subprocess.run(
            cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
            stdin=subprocess.DEVNULL, timeout=PROBE_TIMEOUT,
        )
    except subprocess.TimeoutExpired:
        raise FFmpegStalled(
            f"ffprobe перестал отвечать: {what}. Чтение заголовков файла занимает "
            f"мгновение, а прошло {PROBE_TIMEOUT:.0f} с — похоже, он завис."
        ) from None
    if done.returncode != 0:
        raise FFmpegFailed(f"ffprobe не смог {what}: {_last_words(done.stderr or '')}")
    return done.stdout


def _tool(name: str) -> str:
    # Сначала своя копия: программа доносит FFmpeg сама, если в системе его нет.
    exe = ffmpeg_tools.found(name)
    if not exe:
        raise FFmpegMissing(
            f"Не найден {name}. Установите FFmpeg и добавьте его в PATH: winget install Gyan.FFmpeg"
        )
    return exe


@dataclass
class Track:
    """Одна аудиодорожка внутри файла записи."""

    index: int          # порядковый номер среди аудиопотоков, с 0
    title: str          # tags:title из контейнера (OBS пишет туда имя дорожки)
    language: str
    channels: int

    @property
    def number(self) -> int:
        """Номер дорожки в человеческой нумерации (как в настройках OBS)."""
        return self.index + 1


def probe_tracks(src: Path) -> list[Track]:
    """Список аудиодорожек файла."""
    cmd = [
        _tool("ffprobe"), "-v", "error", "-select_streams", "a",
        "-show_entries", "stream=index,channels:stream_tags=title,language",
        "-of", "json", str(src),
    ]
    raw = _run_probe(cmd, what=f"разобрать дорожки файла {src.name}")
    streams = json.loads(raw).get("streams", [])
    tracks = []
    for i, stream in enumerate(streams):
        tags = stream.get("tags") or {}
        tracks.append(
            Track(
                index=i,
                title=str(tags.get("title", "") or ""),
                language=str(tags.get("language", "") or ""),
                channels=int(stream.get("channels") or 0),
            )
        )
    return tracks


def extract_track(src: Path, track: Track, dst_dir: Path) -> Path:
    """Вытащить дорожку в моно WAV 16 кГц — формат, который ждёт Whisper."""
    dst_dir.mkdir(parents=True, exist_ok=True)
    dst = dst_dir / f"{src.stem}.track{track.number}.wav"
    cmd = [
        _tool("ffmpeg"), "-y", "-loglevel", "error",
        "-i", str(src),
        "-map", f"0:a:{track.index}",
        "-ac", "1", "-ar", "16000",
        "-c:a", "pcm_s16le",
        str(dst),
    ]
    _run_ffmpeg(cmd, what=f"извлечь дорожку {track.number} из {src.name}")
    return dst


def duration_seconds(src: Path) -> float:
    cmd = [
        _tool("ffprobe"), "-v", "error", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1", str(src),
    ]
    out = _run_probe(cmd, what=f"узнать длительность файла {src.name}").strip()
    try:
        return float(out)
    except ValueError:
        return 0.0


def is_silent(wav: Path, threshold_db: float = -50.0) -> bool:
    """Пустая ли дорожка: OBS иногда пишет молчащие дорожки, их незачем распознавать."""
    cmd = [_tool("ffmpeg"), "-i", str(wav), "-af", "volumedetect", "-f", "null", "-"]
    try:
        report = _run_ffmpeg(cmd, what=f"измерить громкость дорожки {wav.name}")
    except FFmpegFailed:
        # Не смогли измерить — считаем дорожку звучащей: лишняя работа
        # распознавания лучше, чем молча выброшенная запись разговора.
        return False
    for line in report.splitlines():
        if "mean_volume:" in line:
            try:
                return float(line.split("mean_volume:")[1].split("dB")[0].strip()) < threshold_db
            except (IndexError, ValueError):
                return False
    return False
