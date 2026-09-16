"""Личные шаблоны переживают обновление и не могут сломать подстановки."""

import pytest

from callsum import prompts


def test_custom_prompt_overrides_only_its_own_template(tmp_path, monkeypatch):
    monkeypatch.setattr(prompts.config, "config_dir", lambda: tmp_path / "config")
    original = prompts.read("map_ru.md")
    custom = "Кусок {index}/{total}:\n{transcript}"

    prompts.save("map_ru.md", custom)

    assert prompts.read("map_ru.md") == custom
    assert prompts.read("summary_ru.md") != custom
    assert (tmp_path / "config" / "prompts" / "map_ru.md").is_file()

    prompts.reset("map_ru.md")

    assert prompts.read("map_ru.md") == original


@pytest.mark.parametrize(
    ("name", "text", "message"),
    [
        ("summary_ru.md", "", "не может быть пустым"),
        ("summary_ru.md", "{meta}", "{transcript}"),
        ("map_ru.md", "{index} {total} {unknown} {transcript}", "неверная подстановка"),
        ("other.md", "текст", "Неизвестный"),
    ],
)
def test_invalid_custom_prompt_is_refused(tmp_path, monkeypatch, name, text, message):
    monkeypatch.setattr(prompts.config, "config_dir", lambda: tmp_path / "config")

    with pytest.raises(prompts.PromptError, match=message):
        prompts.save(name, text)


def test_manually_broken_custom_prompt_is_refused_on_read(tmp_path, monkeypatch):
    monkeypatch.setattr(prompts.config, "config_dir", lambda: tmp_path / "config")
    custom = tmp_path / "config" / "prompts" / "summary_ru.md"
    custom.parent.mkdir(parents=True)
    custom.write_text("Только шапка: {meta}", encoding="utf-8")

    with pytest.raises(prompts.PromptError, match=r"\{transcript\}"):
        prompts.read("summary_ru.md")

    item = next(item for item in prompts.report()["items"] if item["name"] == "summary_ru.md")
    assert item["content"] == "Только шапка: {meta}"
    assert "{transcript}" in item["error"]
