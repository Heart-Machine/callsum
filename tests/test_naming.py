"""Тесты шаблона имени папки с результатами."""

from datetime import datetime
from pathlib import Path

from callsum.naming import folder_name, sanitize

WHEN = datetime(2026, 9, 12, 14, 3, 11)
SRC = Path(r"D:\recordings\2026-09-12 14-03-11.mkv")


def test_default_template_repeats_the_recording_name():
    assert folder_name(SRC, "{name}", WHEN) == "2026-09-12 14-03-11"


def test_date_and_time_come_from_the_recording():
    assert folder_name(SRC, "{date} {time}", WHEN) == "2026-09-12 14-03-11"
    assert folder_name(SRC, "{datetime} клиент", WHEN) == "2026-09-12 14-03-11 клиент"


def test_empty_template_falls_back_to_the_name():
    assert folder_name(SRC, "", WHEN) == "2026-09-12 14-03-11"


def test_unknown_placeholder_is_left_as_is():
    """Опечатка в шаблоне не должна ронять обработку уже записанного созвона."""
    assert folder_name(SRC, "{nmae} {date}", WHEN) == "{nmae} 2026-09-12"


def test_forbidden_characters_are_replaced():
    assert sanitize('соз:вон/встреча?') == "соз-вон-встреча-"
    assert folder_name(SRC, "{name} | клиент", WHEN) == "2026-09-12 14-03-11 - клиент"


def test_trailing_dot_and_space_are_trimmed():
    """Windows не создаёт папки с точкой или пробелом в конце имени."""
    assert sanitize("созвон. ") == "созвон"
    assert sanitize("  два   пробела  ") == "два пробела"
