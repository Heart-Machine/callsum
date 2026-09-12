"""Тесты параметра [view] markdown_app: чем открывать протоколы."""

from pathlib import Path

from callsum.view import build_open_command

DOC = Path(r"D:\Записи\созвон\summary.md")


def test_empty_setting_uses_system_association():
    assert build_open_command("", DOC) == ["cmd", "/c", "start", "", str(DOC)]


def test_program_name_gets_file_as_argument():
    assert build_open_command("notepad++", DOC) == ["notepad++", str(DOC)]


def test_quoted_path_with_spaces_stays_one_argument():
    command = build_open_command(r'"C:\Program Files\Typora\Typora.exe"', DOC)
    assert command == [r"C:\Program Files\Typora\Typora.exe", str(DOC)]


def test_existing_path_with_spaces_survives_without_quotes(tmp_path):
    exe = tmp_path / "Program Files" / "Typora.exe"
    exe.parent.mkdir()
    exe.write_text("", encoding="utf-8")
    assert build_open_command(str(exe), DOC) == [str(exe), str(DOC)]


def test_template_puts_file_where_asked():
    assert build_open_command("code -r {file}", DOC) == ["code", "-r", str(DOC)]


def test_link_percent_encodes_the_path():
    command = build_open_command("obsidian://open?path={file}", DOC)
    assert command[:4] == ["cmd", "/c", "start", ""]
    assert command[4].startswith("obsidian://open?path=D%3A%5C")
    assert " " not in command[4]
