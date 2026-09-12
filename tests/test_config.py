"""Тесты конфигурации: пример, автосоздание и слияние с дефолтами."""

import shutil
import tomllib

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

    assert config.ensure_config(target) is True
    assert target.read_text(encoding="utf-8") == (
        (tmp_path / config.EXAMPLE_NAME).read_text(encoding="utf-8")
    )


def test_existing_config_is_never_overwritten(tmp_path):
    """Личные настройки важнее примера — второй запуск не должен их затирать."""
    shutil.copyfile(config.ROOT / config.EXAMPLE_NAME, tmp_path / config.EXAMPLE_NAME)
    target = tmp_path / "config.toml"
    target.write_text("[view]\nmarkdown_app = 'notepad++'\n", encoding="utf-8")

    assert config.ensure_config(target) is False
    assert "notepad++" in target.read_text(encoding="utf-8")


def test_missing_example_is_not_an_error(tmp_path):
    assert config.ensure_config(tmp_path / "config.toml") is False


def test_partial_config_falls_back_to_defaults(tmp_path):
    """После обновления проекта в старом config.toml не будет новых ключей."""
    partial = tmp_path / "config.toml"
    partial.write_text("[summary]\nmodel = 'qwen3:8b'\n", encoding="utf-8")

    cfg = config.load(partial)
    assert cfg.summary["model"] == "qwen3:8b"
    assert cfg.summary["num_ctx"] == config.DEFAULTS["summary"]["num_ctx"]
    assert cfg.transcribe["model"] == config.DEFAULTS["transcribe"]["model"]
