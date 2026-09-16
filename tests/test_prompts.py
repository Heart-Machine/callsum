"""Шаблоны протокола не должны терять ограничения против выдуманных фактов."""

import pytest

from callsum.config import RESOURCES


@pytest.mark.parametrize(
    ("name", "values"),
    [
        ("summary_ru.md", {"meta": "", "transcript": ""}),
        ("map_ru.md", {"index": 1, "total": 1, "transcript": ""}),
        ("reduce_ru.md", {"meta": "", "notes": ""}),
    ],
)
def test_prompt_templates_accept_their_placeholders(name, values):
    template = (RESOURCES / "prompts" / name).read_text(encoding="utf-8")

    assert template.format(**values)


@pytest.mark.parametrize("name", ["summary_ru.md", "map_ru.md", "reduce_ru.md"])
def test_prompts_require_source_evidence(name):
    template = (RESOURCES / "prompts" / name).read_text(encoding="utf-8")

    assert "подтвержд" in template.lower()
    assert any(
        phrase in template.lower()
        for phrase in ("не придумывай", "не заполняй", "не добавляй")
    )
