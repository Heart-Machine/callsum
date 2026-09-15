"""Тесты шаблона имени папки с результатами."""

from datetime import datetime
import json

import pytest

from callsum.naming import folder_name, result_dir, sanitize

WHEN = datetime(2026, 9, 12, 14, 3, 11)


@pytest.fixture
def src(tmp_path):
    """Запись во временной папке: тест не должен зависеть от буквы диска."""
    return tmp_path / "recordings" / "2026-09-12 14-03-11.mkv"


def test_default_template_repeats_the_recording_name(src):
    assert folder_name(src, "{name}", WHEN) == "2026-09-12 14-03-11"


def test_date_and_time_come_from_the_recording(src):
    assert folder_name(src, "{date} {time}", WHEN) == "2026-09-12 14-03-11"
    assert folder_name(src, "{datetime} клиент", WHEN) == "2026-09-12 14-03-11 клиент"


def test_empty_template_falls_back_to_the_name(src):
    assert folder_name(src, "", WHEN) == "2026-09-12 14-03-11"


def test_unknown_placeholder_is_left_as_is(src):
    """Опечатка в шаблоне не должна ронять обработку уже записанного созвона."""
    assert folder_name(src, "{nmae} {date}", WHEN) == "{nmae} 2026-09-12"


def test_result_directory_does_not_reuse_another_recordings_folder(tmp_path):
    first = tmp_path / "клиент" / "meeting.mkv"
    second = tmp_path / "внутренний" / "meeting.mp4"
    first.parent.mkdir()
    second.parent.mkdir()
    first.write_bytes(b"")
    second.write_bytes(b"")
    occupied = tmp_path / "out" / "meeting"
    occupied.mkdir(parents=True)
    (occupied / "transcript.json").write_text(
        '{"source": ' + json.dumps(str(first.resolve()), ensure_ascii=False) + '}', encoding="utf-8")

    assert result_dir(first, tmp_path / "out", "{name}") == occupied
    assert result_dir(second, tmp_path / "out", "{name}") == tmp_path / "out" / "meeting (2)"


def test_forbidden_characters_are_replaced(src):
    assert sanitize('соз:вон/встреча?') == "соз-вон-встреча-"
    assert folder_name(src, "{name} | клиент", WHEN) == "2026-09-12 14-03-11 - клиент"


def test_trailing_dot_and_space_are_trimmed():
    """Windows не создаёт папки с точкой или пробелом в конце имени."""
    assert sanitize("созвон. ") == "созвон"
    assert sanitize("  два   пробела  ") == "два пробела"


def test_time_is_taken_from_the_recording_when_not_given(tmp_path):
    """Без явной отметки времени берётся время файла — значит, файл нужен настоящий."""
    recording = tmp_path / "созвон.mkv"
    recording.write_bytes(b"")

    assert folder_name(recording, "{name}") == "созвон"
    assert folder_name(recording, "{date}").count("-") == 2
