"""Тесты правки config.toml: значения меняются, файл остаётся человеческим."""

import tomllib

from callsum import settings


def test_value_changes_and_comments_stay():
    text = (
        "# Папки программы.\n"
        "[paths]\n"
        "# Куда OBS пишет записи.\n"
        'recordings = "recordings"\n'
        'out = "out"\n'
    )

    result = settings.apply(text, {"paths": {"out": r"D:\созвоны\out"}})

    assert "# Куда OBS пишет записи." in result
    assert 'recordings = "recordings"' in result
    assert tomllib.loads(result)["paths"]["out"] == r"D:\созвоны\out"


def test_windows_path_stays_readable_toml():
    """Путь в двойных кавычках потребовал бы удвоения слешей — и ломал бы файл."""
    result = settings.apply("[paths]\nout = 'out'\n", {"paths": {"out": r"C:\Users\User\callsum"}})

    assert "out = 'C:\\Users\\User\\callsum'" in result
    assert tomllib.loads(result)["paths"]["out"] == r"C:\Users\User\callsum"


def test_same_key_in_other_section_is_not_touched():
    """`host` есть и в [obs], и в [summary] — правка не должна попасть не туда."""
    text = (
        "[obs]\n"
        'host = "127.0.0.1"\n'
        "\n"
        "[summary]\n"
        'host = "http://127.0.0.1:11434"\n'
        'model = "qwen3:14b"\n'
    )

    result = settings.apply(text, {"summary": {"host": "http://10.0.0.5:11434"}})

    data = tomllib.loads(result)
    assert data["obs"]["host"] == "127.0.0.1"
    assert data["summary"]["host"] == "http://10.0.0.5:11434"
    assert data["summary"]["model"] == "qwen3:14b"


def test_missing_key_is_added_to_its_own_section():
    text = "[view]\n# Чем открывать протоколы.\n\n[obs]\nprofile = 'callsum'\n"

    result = settings.apply(text, {"view": {"markdown_app": "notepad++"}})

    data = tomllib.loads(result)
    assert data["view"]["markdown_app"] == "notepad++"
    assert data["obs"]["profile"] == "callsum"
    # Новая строка встаёт внутри своей секции, а не за пустой строкой перед [obs].
    assert result.index("markdown_app") < result.index("[obs]")


def test_missing_section_is_added_at_the_end():
    result = settings.apply("[paths]\nout = 'out'\n", {"speakers": {"1": "Я", "2": "Коллега"}})

    data = tomllib.loads(result)
    assert data["speakers"] == {"1": "Я", "2": "Коллега"}
    assert data["paths"]["out"] == "out"


def test_types_are_written_as_toml():
    text = "[transcribe]\nvad = true\nbeam_size = 5\nsplit_gap = 1.5\n[audio]\nextensions = []\n"

    result = settings.apply(text, {
        "transcribe": {"vad": False, "beam_size": 3, "split_gap": 2.0},
        "audio": {"extensions": [".mkv", ".mp4"]},
    })

    data = tomllib.loads(result)
    assert data["transcribe"] == {"vad": False, "beam_size": 3, "split_gap": 2.0}
    assert data["audio"]["extensions"] == [".mkv", ".mp4"]


def test_comment_at_the_end_of_the_line_survives():
    text = '[summary]\nmodel = "qwen3:14b"  # можно поменять\n'

    result = settings.apply(text, {"summary": {"model": "qwen3:32b"}})

    assert "# можно поменять" in result
    assert tomllib.loads(result)["summary"]["model"] == "qwen3:32b"


def test_hash_inside_value_is_not_a_comment():
    text = "[view]\nmarkdown_app = 'obsidian://open?vault=work#заметки'\n"

    result = settings.apply(text, {"view": {"markdown_app": "notepad"}})

    assert tomllib.loads(result)["view"]["markdown_app"] == "notepad"
    assert "#заметки" not in result


def test_real_example_file_survives_a_round_trip():
    """Главная проверка: настоящий config.example.toml после правки читается."""
    from callsum import config

    text = (config.RESOURCES / config.EXAMPLE_NAME).read_text(encoding="utf-8")

    result = settings.apply(text, {
        "paths": {"out": r"D:\Home\созвоны", "folder_template": "{date} {name}"},
        "summary": {"model": "qwen3:32b"},
        "view": {"markdown_app": r"C:\Program Files\Typora\Typora.exe"},
    })

    data = tomllib.loads(result)
    assert data["paths"]["out"] == r"D:\Home\созвоны"
    assert data["paths"]["folder_template"] == "{date} {name}"
    assert data["summary"]["model"] == "qwen3:32b"
    assert data["view"]["markdown_app"] == r"C:\Program Files\Typora\Typora.exe"
    # Комментарии на месте: файл остался тем, который правят руками.
    assert result.count("#") > 20
