"""Тесты параметра [view] markdown_app: чем открывать протоколы."""

import pytest

from callsum import config
from callsum.view import build_open_command


@pytest.fixture
def doc(tmp_path):
    """Протокол во временной папке: буква диска к делу не относится."""
    return tmp_path / "созвон" / "summary.md"


def test_empty_setting_uses_system_association(doc):
    assert build_open_command("", doc) == ["cmd", "/c", "start", "", str(doc)]


def test_program_name_gets_file_as_argument(doc):
    assert build_open_command("notepad++", doc) == ["notepad++", str(doc)]


def test_quoted_path_with_spaces_stays_one_argument(doc):
    command = build_open_command(r'"C:\Program Files\Typora\Typora.exe"', doc)
    assert command == [r"C:\Program Files\Typora\Typora.exe", str(doc)]


def test_existing_path_with_spaces_survives_without_quotes(tmp_path, doc):
    exe = tmp_path / "Program Files" / "Typora.exe"
    exe.parent.mkdir()
    exe.write_text("", encoding="utf-8")
    assert build_open_command(str(exe), doc) == [str(exe), str(doc)]


def test_template_puts_file_where_asked(doc):
    assert build_open_command("code -r {file}", doc) == ["code", "-r", str(doc)]


def test_link_percent_encodes_the_path(doc):
    command = build_open_command("obsidian://open?path={file}", doc)
    assert command[:4] == ["cmd", "/c", "start", ""]
    # Путь целиком закодирован: пробелов и разделителей в ссылке быть не должно.
    assert command[4].startswith("obsidian://open?path=")
    assert " " not in command[4] and chr(92) not in command[4]


def test_broken_config_gives_a_readable_error(tmp_path):
    """Окно запускается без консоли — ошибка должна объяснять себя сама."""
    bad = tmp_path / "config.toml"
    # Частая ошибка: путь Windows в двойных кавычках, где слеш не удвоен.
    bad.write_text('[view]\nmarkdown_app = "C:\\Program Files\\Typora.exe"\n', encoding="utf-8")
    with pytest.raises(config.ConfigError, match="одинарных кавычках"):
        config.load(bad)


def test_single_quoted_windows_path_is_accepted(tmp_path):
    """Одинарные кавычки в TOML — литеральная строка, удваивать слеши не нужно."""
    good = tmp_path / "config.toml"
    good.write_text("[view]\nmarkdown_app = 'C:\\Program Files\\Typora.exe'\n", encoding="utf-8")
    assert config.load(good).view["markdown_app"] == r"C:\Program Files\Typora.exe"