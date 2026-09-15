"""Правка config.toml по одному значению, с сохранением файла как он есть.

Настройки задаются из окна приложения, но файл остаётся человеческим: в нём
комментарии, которые объясняют каждый параметр, и порядок, к которому привык
тот, кто правит его руками. Поэтому файл не перезаписывается целиком из данных,
а правится построчно: меняется значение, всё остальное остаётся нетронутым.
"""

from __future__ import annotations

from typing import Any

Changes = dict[str, dict[str, Any]]


def format_value(value: Any) -> str:
    """Значение в записи TOML.

    Пути Windows пишутся в одинарных кавычках: в двойных обратный слеш пришлось
    бы удваивать, и именно на этом чаще всего ломается файл, если его правят
    руками.
    """
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(value)
    if isinstance(value, (list, tuple)):
        return "[" + ", ".join(format_value(item) for item in value) + "]"

    text = str(value)
    if "\\" in text and "'" not in text and "\n" not in text:
        return f"'{text}'"
    escaped = text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
    return f'"{escaped}"'


def _section_of(line: str) -> str | None:
    comment = _comment_start(line)
    stripped = line[:comment].strip() if comment is not None else line.strip()
    if stripped.startswith("[") and stripped.endswith("]"):
        return stripped[1:-1].strip()
    return None


def _key_of(line: str) -> str | None:
    stripped = line.strip()
    if not stripped or stripped.startswith("#") or "=" not in stripped:
        return None
    return stripped.split("=", 1)[0].strip().strip('"').strip("'")


def _trailing_comment(line: str) -> str:
    """Комментарий в конце строки, если он там есть и не внутри кавычек."""
    position = _comment_start(line)
    return "  " + line[position:].strip() if position is not None else ""


def _comment_start(line: str) -> int | None:
    """Позиция комментария TOML вне строкового литерала."""
    quote: str | None = None
    escaped = False
    for position, symbol in enumerate(line):
        if quote is not None:
            if quote == '"' and escaped:
                escaped = False
            elif quote == '"' and symbol == "\\":
                escaped = True
            elif symbol == quote:
                quote = None
        elif symbol in "\"'":
            quote = symbol
        elif symbol == "#":
            return position
    return None


def apply(text: str, changes: Changes) -> str:
    """Вернуть текст настроек с изменёнными значениями.

    Ключ ищется только внутри своей секции: `host` есть и в [obs], и в [summary],
    `model` — и в [transcribe], и в [summary]. Правка по одному имени ключа
    попала бы не туда.
    """
    remaining: Changes = {
        section: dict(values) for section, values in changes.items() if values
    }
    lines = text.splitlines()
    result: list[str] = []
    section = ""

    for index, line in enumerate(lines):
        found = _section_of(line)
        if found is not None:
            _close_section(result, remaining.pop(section, {}))
            section = found
            result.append(line)
            continue

        key = _key_of(line)
        pending = remaining.get(section, {})
        if key is not None and key in pending:
            indent = line[: len(line) - len(line.lstrip())]
            value = format_value(pending.pop(key))
            result.append(f"{indent}{key} = {value}{_trailing_comment(line)}")
            continue

        result.append(line)

    # Незакрытая секция в конце файла и секции, которых в файле не было.
    _close_section(result, remaining.pop(section, {}))
    for name, values in remaining.items():
        if not values:
            continue
        if result and result[-1].strip():
            result.append("")
        result.append(f"[{name}]")
        result.extend(_tail(values))

    ending = "\n" if text.endswith("\n") or not text else ""
    return "\n".join(result) + ending


def _close_section(result: list[str], values: dict[str, Any]) -> None:
    """Дописать ключи, которых в секции не нашлось, в её конец.

    Пустые строки перед следующей секцией сохраняются: новые строки встают
    к своим соседям, а не за разделителем.
    """
    if not values:
        return
    blanks = 0
    while result and not result[-1].strip():
        result.pop()
        blanks += 1
    result.extend(_tail(values))
    result.extend([""] * blanks)


def _tail(values: dict[str, Any]) -> list[str]:
    return [f"{key} = {format_value(value)}" for key, value in values.items()]
