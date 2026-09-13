"""Тесты сторожа за ffmpeg: зависший процесс снимается, работающий — нет."""


import sys
import time

import pytest

from callsum import audio


def python(code: str) -> list[str]:
    """Команда-подделка вместо ffmpeg: ведёт себя так, как нужно тесту."""
    return [sys.executable, "-c", code]


def test_stalled_process_is_killed_with_advice():
    """Молчащий процесс снимается, а сообщение говорит, что делать дальше."""
    started = time.monotonic()

    with pytest.raises(audio.FFmpegStalled) as failure:
        audio._supervise(
            python("import time; time.sleep(30)"),
            what="извлечь дорожку 2 из созвон.mkv",
            stall_seconds=1.0,
        )

    assert time.monotonic() - started < 10, "сторож обязан сработать сразу, а не ждать конца"
    assert "извлечь дорожку 2" in str(failure.value)
    assert "ещё раз" in str(failure.value)


def test_work_in_progress_is_not_killed():
    """Главное свойство: сторож смотрит на движение, а не на общее время.

    Процесс работает дольше времени ожидания, но отчитывается — значит, живой.
    Обычный таймаут по общему времени убил бы такую работу.
    """
    code = (
        "import sys, time\n"
        "for _ in range(10):\n"
        "    sys.stdout.write('out_time_ms=1\\n'); sys.stdout.flush(); time.sleep(0.2)\n"
    )

    audio._supervise(python(code), what="извлечь дорожку", stall_seconds=1.0)


def test_failure_repeats_what_ffmpeg_said():
    """В окне должна оказаться жалоба ffmpeg, а не длина командной строки."""
    code = (
        "import sys\n"
        "sys.stderr.write('Invalid data found when processing input\\n')\n"
        "sys.exit(3)\n"
    )

    with pytest.raises(audio.FFmpegFailed) as failure:
        audio._supervise(python(code), what="извлечь дорожку 1", stall_seconds=5.0)

    assert "Invalid data found" in str(failure.value)
    assert "извлечь дорожку 1" in str(failure.value)


def test_silent_failure_has_at_least_an_explanation():
    with pytest.raises(audio.FFmpegFailed) as failure:
        audio._supervise(python("raise SystemExit(1)"), what="что-то сделать", stall_seconds=5.0)

    assert "не объяснил причину" in str(failure.value)


def test_ffmpeg_is_told_to_report_progress_and_to_leave_stdin_alone():
    """Без `-progress` сторожу не за чем следить, без `-nostdin` ffmpeg может
    съесть команду, адресованную ядру: его stdin — труба от приложения."""
    command = audio._watched(["ffmpeg", "-y", "-i", "запись.mkv"])

    assert command[0] == "ffmpeg"
    assert "-nostdin" in command
    assert command[command.index("-progress") + 1] == "pipe:1"
    assert command[-3:] == ["-y", "-i", "запись.mkv"]


def test_probe_that_hangs_is_not_waited_for_forever(monkeypatch):
    monkeypatch.setattr(audio, "PROBE_TIMEOUT", 0.5)

    with pytest.raises(audio.FFmpegStalled) as failure:
        audio._run_probe(python("import time; time.sleep(30)"), what="разобрать дорожки")

    assert "разобрать дорожки" in str(failure.value)


def test_probe_failure_repeats_the_complaint():
    code = "import sys; sys.stderr.write('No such file\\n'); sys.exit(1)"

    with pytest.raises(audio.FFmpegFailed) as failure:
        audio._run_probe(python(code), what="узнать длительность")

    assert "No such file" in str(failure.value)


def test_unmeasurable_track_counts_as_sounding(monkeypatch, tmp_path):
    """Не смогли измерить громкость — запись всё равно распознаём.

    Молча выбросить разговор хуже, чем потратить время на тишину.
    """
    monkeypatch.setattr(audio, "_tool", lambda name: name)

    def refuse(*args, **kwargs):
        raise audio.FFmpegFailed("ffmpeg не смог измерить громкость")

    monkeypatch.setattr(audio, "_run_ffmpeg", refuse)

    assert audio.is_silent(tmp_path / "дорожка.wav") is False


def test_quiet_track_is_recognised_as_silent(monkeypatch, tmp_path):
    monkeypatch.setattr(audio, "_tool", lambda name: name)
    monkeypatch.setattr(
        audio, "_run_ffmpeg",
        lambda *args, **kwargs: "[Parsed_volumedetect_0 @ 0] mean_volume: -73.4 dB\n",
    )

    assert audio.is_silent(tmp_path / "дорожка.wav") is True


def test_stalled_process_does_not_survive_the_watchdog(tmp_path):
    """Снятый процесс обязан умереть, а не остаться держать файл.

    Проверяется по-настоящему: пока процесс жив, Windows не даёт удалить
    открытый им файл — значит, удаление удалось только если его больше нет.
    """
    busy = tmp_path / "занятый.wav"
    code = f"import time\nhandle = open(r'{busy}', 'w')\ntime.sleep(30)\n"

    with pytest.raises(audio.FFmpegStalled):
        audio._supervise(python(code), what="проверка", stall_seconds=1.0)

    # Освобождение файла отстаёт от завершения процесса: после kill за него
    # ещё может держаться антивирус. Живой процесс держал бы файл вечно,
    # поэтому ждём недолго — и всё равно проверяем, что он отпустил.
    for _ in range(50):
        try:
            busy.unlink()
            return
        except PermissionError:
            time.sleep(0.1)

    pytest.fail("файл всё ещё занят — процесс пережил сторожа")
