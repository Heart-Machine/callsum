"""Тесты конфигурации: пример, автосоздание и слияние с дефолтами."""

import shutil
import tomllib
from pathlib import Path

from callsum import config


def _flatten(data, prefix=""):
    flat = {}
    for key, value in data.items():
        if isinstance(value, dict):
            flat.update(_flatten(value, f"{prefix}{key}."))
        else:
            flat[f"{prefix}{key}"] = value
    return flat


def test_example_matches_defaults_in_code():
    """Пример и DEFAULTS обязаны совпадать: иначе документация врёт про поведение."""
    example = tomllib.loads((config.ROOT / config.EXAMPLE_NAME).read_text(encoding="utf-8"))
    assert _flatten(example) == _flatten(config.DEFAULTS)


def test_config_is_created_from_example(tmp_path):
    shutil.copyfile(config.ROOT / config.EXAMPLE_NAME, tmp_path / config.EXAMPLE_NAME)
    target = tmp_path / "config.toml"

    assert config.ensure_config(target) is not None
    assert target.read_text(encoding="utf-8") == (
        (tmp_path / config.EXAMPLE_NAME).read_text(encoding="utf-8")
    )


def test_existing_config_is_never_overwritten(tmp_path):
    """Личные настройки важнее примера — второй запуск не должен их затирать."""
    shutil.copyfile(config.ROOT / config.EXAMPLE_NAME, tmp_path / config.EXAMPLE_NAME)
    target = tmp_path / "config.toml"
    target.write_text("[view]\nmarkdown_app = 'notepad++'\n", encoding="utf-8")

    assert config.ensure_config(target) is None
    assert "notepad++" in target.read_text(encoding="utf-8")


def test_missing_example_is_not_an_error(tmp_path, monkeypatch):
    empty = tmp_path / "resources"
    empty.mkdir()
    monkeypatch.setattr(config, "RESOURCES", empty)
    assert config.ensure_config(tmp_path / "config.toml") is None


def test_example_is_taken_from_the_bundle_when_missing_next_to_config(tmp_path):
    """В собранном ядре пример лежит внутри сборки, а config.toml — рядом с exe."""
    target = tmp_path / "config.toml"
    assert config.ensure_config(target) is not None
    assert target.read_text(encoding="utf-8") == (
        (config.RESOURCES / config.EXAMPLE_NAME).read_text(encoding="utf-8")
    )


def _installed(tmp_path, monkeypatch):
    """Притвориться установленным приложением: собранное ядро в папке программы."""
    program = tmp_path / "Programs" / "callsum"
    program.mkdir(parents=True)
    monkeypatch.setattr(config.sys, "frozen", True, raising=False)
    monkeypatch.setattr(config.sys, "executable", str(program / "callsum-core.exe"))
    monkeypatch.setenv("APPDATA", str(tmp_path / "AppData" / "Roaming"))
    return program


def test_installed_settings_live_in_the_user_profile(tmp_path, monkeypatch):
    """Внутри папки программы настройкам не место: обновление ставится поверх неё.

    Раньше config.toml лежал рядом с ядром, и пересборка стирала его вместе
    со всем, что накопилось в папке сборки.
    """
    program = _installed(tmp_path, monkeypatch)

    assert config.config_dir() == tmp_path / "AppData" / "Roaming" / "callsum"
    assert config.config_path().parent != program


def test_installed_recordings_live_outside_documents(tmp_path, monkeypatch):
    """Записи созвонов не должны молча уезжать в облако из «Документов»."""
    _installed(tmp_path, monkeypatch)

    assert config.data_dir() == Path.home() / "callsum"


def test_development_keeps_everything_in_the_repository(tmp_path, monkeypatch):
    monkeypatch.setattr(config.sys, "frozen", False, raising=False)

    repository = Path(config.__file__).resolve().parent.parent
    assert config.config_dir() == repository
    assert config.data_dir() == repository


def test_settings_of_the_previous_version_are_taken_along(tmp_path, monkeypatch):
    """Пути и модель пользователь задавал сам — при переезде их нельзя терять."""
    program = _installed(tmp_path, monkeypatch)
    (program / "config.toml").write_text(
        "[paths]\nout = 'D:\\созвоны'\n", encoding="utf-8")

    target = config.config_path()
    assert config.ensure_config(target) is not None
    assert "D:\\созвоны" in target.read_text(encoding="utf-8")


def test_example_is_used_when_there_is_nothing_to_take_along(tmp_path, monkeypatch):
    _installed(tmp_path, monkeypatch)

    target = config.config_path()
    assert config.ensure_config(target) is not None
    assert target.read_text(encoding="utf-8") == (
        (config.RESOURCES / config.EXAMPLE_NAME).read_text(encoding="utf-8")
    )


def test_partial_config_falls_back_to_defaults(tmp_path):
    """После обновления проекта в старом config.toml не будет новых ключей."""
    partial = tmp_path / "config.toml"
    partial.write_text("[summary]\nmodel = 'qwen3:8b'\n", encoding="utf-8")

    cfg = config.load(partial)
    assert cfg.summary["model"] == "qwen3:8b"
    assert cfg.summary["num_ctx"] == config.DEFAULTS["summary"]["num_ctx"]
    assert cfg.transcribe["model"] == config.DEFAULTS["transcribe"]["model"]


def test_moved_settings_are_reported_as_moved(tmp_path, monkeypatch):
    """«Перенёс ваши прежние» и «создал из примера» — разные новости."""
    program = _installed(tmp_path, monkeypatch)
    previous = program / "config.toml"
    previous.write_text("[paths]\nout = 'своё'\n", encoding="utf-8")

    cfg = config.load()

    assert cfg.created_from == previous
    assert cfg.paths["out"] == "своё"
