"""Шаблоны протокола: поставляемые с приложением и пользовательские версии."""

from __future__ import annotations

from pathlib import Path

from . import config


class PromptError(RuntimeError):
    """Шаблон нельзя сохранить: он пустой или сломал обязательные подстановки."""


FIELDS = {
    "summary_ru.md": {"meta": "", "transcript": ""},
    "map_ru.md": {"index": 1, "total": 1, "transcript": ""},
    "reduce_ru.md": {"meta": "", "notes": ""},
}


def folder() -> Path:
    """Папка личных шаблонов: обновление приложения её не затрагивает."""
    return config.config_dir() / "prompts"


def _known(name: str) -> None:
    if name not in FIELDS:
        raise PromptError(f"Неизвестный шаблон: {name}")


def _builtin(name: str) -> Path:
    _known(name)
    return config.RESOURCES / "prompts" / name


def _custom(name: str) -> Path:
    _known(name)
    return folder() / name


def _content(name: str) -> str:
    """Прочитать версию шаблона, не оценивая её: это нужно редактору для починки."""
    custom = _custom(name)
    source = custom if custom.is_file() else _builtin(name)
    try:
        return source.read_text(encoding="utf-8")
    except OSError as exc:
        raise PromptError(f"Не удалось прочитать шаблон {name}: {exc}") from exc


def read(name: str) -> str:
    """Вернуть рабочий шаблон: ошибочная личная версия не попадёт в Ollama."""
    text = _content(name)
    _validate(name, text)
    return text


def _validate(name: str, text: str) -> None:
    _known(name)
    if not isinstance(text, str):
        raise PromptError("Текст шаблона должен быть строкой")
    if not text.strip():
        raise PromptError("Шаблон не может быть пустым")

    required = [f"{{{field}}}" for field in FIELDS[name] if f"{{{field}}}" not in text]
    if required:
        raise PromptError(
            f"В шаблоне {name} не хватает подстановок: {', '.join(required)}"
        )
    try:
        text.format(**FIELDS[name])
    except (AttributeError, IndexError, KeyError, ValueError) as exc:
        raise PromptError(f"В шаблоне {name} неверная подстановка: {exc}") from exc


def save(name: str, text: str) -> None:
    """Проверить и атомарно сохранить личную версию одного шаблона."""
    _validate(name, text)
    destination = _custom(name)
    try:
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_suffix(".md.new")
        temporary.write_text(text, encoding="utf-8")
        temporary.replace(destination)
    except OSError as exc:
        raise PromptError(f"Не удалось сохранить шаблон {name}: {exc}") from exc


def reset(name: str) -> None:
    """Убрать личную копию: со следующего запроса используется версия релиза."""
    custom = _custom(name)
    try:
        custom.unlink(missing_ok=True)
    except OSError as exc:
        raise PromptError(f"Не удалось восстановить шаблон {name}: {exc}") from exc


def report() -> dict:
    """Все шаблоны для окна: текст, источник и папка пользовательских копий."""
    return {
        "folder": str(folder()),
        "items": [_report_item(name) for name in FIELDS],
    }


def _report_item(name: str) -> dict:
    text = _content(name)
    try:
        _validate(name, text)
        error = ""
    except PromptError as exc:
        error = str(exc)
    return {"name": name, "content": text, "custom": _custom(name).is_file(), "error": error}
