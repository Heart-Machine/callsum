"""Тесты шаблона имени папки с результатами."""

from datetime import datetime

import pytest

from callsum.naming import folder_name, sanitize

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
