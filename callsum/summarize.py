"""Саммари транскрипта локальной моделью через Ollama."""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.request

from . import prompts


class OllamaError(RuntimeError):
    pass


class EmptyTranscriptError(OllamaError):
    """Расшифровка готова, но в ней нет реплик для протокола."""


EMPTY_TRANSCRIPT_MESSAGE = "В расшифровке нет распознанной речи — протокол не создан."


def dialog_from_document(raw: str) -> str:
    """Достать реплики из transcript.md, не выдавая его шапку за диалог."""
    _, separator, dialog = raw.partition("\n---\n")
    return (dialog if separator else raw).strip()


def _fetch(target, host: str, timeout: int, model: str = "") -> dict:
    """Сходить к Ollama и вернуть разобранный ответ.

    Наружу отдаётся ровно одна ошибка — OllamaError, — и это не аккуратность,
    а условие работы: пайплайн ловит только её, чтобы сказать «протокол не
    собрался, расшифровка на месте». Всё, что проходит мимо, роняет обработку
    целиком — и готовый транскрипт оказывается под надписью «Ошибка обработки».

    Ловить `URLError` для этого мало. Ответ, оборванный по времени, приходит
    голым `TimeoutError`; перезапущенная посреди ответа Ollama — `OSError`;
    а если на порту отвечает не она, разбор падает на `ValueError`.
    """
    try:
        with urllib.request.urlopen(target, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        # Отказ с ответом: причина лежит в теле, а не в коде HTTP.
        raise OllamaError(_refusal(exc, host, model)) from exc
    except TimeoutError as exc:
        raise OllamaError(
            f"Ollama не ответила за {timeout} с ({host}). Если созвон длинный, "
            "а модель большая, поднимите [summary] timeout_seconds в настройках."
        ) from exc
    except (urllib.error.URLError, OSError) as exc:
        reason = getattr(exc, "reason", None) or exc
        raise OllamaError(
            f"Ollama не отвечает на {host}: {reason}. "
            "Проверьте, что сервис запущен (`ollama serve`)."
        ) from exc
    except ValueError as exc:
        raise OllamaError(
            f"С {host} пришёл ответ, который не разобрать: {exc}. "
            "Обычно так отвечает не Ollama, а прокси или другая программа на этом порту."
        ) from exc


def _refusal(exc: urllib.error.HTTPError, host: str, model: str) -> str:
    """Что сказать про отказ Ollama.

    Прежде любой её отказ объяснялся одинаково — «проверьте, что сервис
    запущен». На отсутствующую модель Ollama отвечает кодом 404, и совет
    запустить уже запущенный сервис только сбивал с толку.
    """
    said = _said(exc)
    if exc.code == 404 and model:
        return (
            f"В Ollama нет модели {model}" + (f" ({said})" if said else "") + ". "
            f"Загрузите её один раз: ollama pull {model} — "
            "либо выберите другую в настройках, [summary] model."
        )
    if said:
        return f"Ollama отказалась отвечать ({host}): {said}"
    return f"Ollama ответила ошибкой {exc.code} на {host}: {exc.reason}"


def _said(exc: urllib.error.HTTPError) -> str:
    """Объяснение самой Ollama из тела ответа — оно точнее любого нашего."""
    try:
        data = json.loads(exc.read().decode("utf-8", "replace"))
    except (OSError, ValueError):
        return ""
    return str(data.get("error", "")) if isinstance(data, dict) else ""


def _post(host: str, path: str, payload: dict, timeout: int) -> dict:
    request = urllib.request.Request(
        f"{host.rstrip('/')}{path}",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    return _fetch(request, host, timeout, str(payload.get("model", "")))


def available_models(host: str, timeout: int = 10) -> list[str]:
    data = _fetch(f"{host.rstrip('/')}/api/tags", host, timeout)
    return [m.get("name", "") for m in data.get("models", [])]


def _strip_think(text: str) -> str:
    """Убрать блок <think>…</think>, если модель всё же «подумала» вслух."""
    return re.sub(r"<think>.*?</think>", "", text, flags=re.DOTALL).strip()


def _generate(cfg_summary: dict, prompt: str) -> str:
    payload = {
        "model": cfg_summary["model"],
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
        "think": bool(cfg_summary.get("think", False)),
        # Сколько модель держится в видеопамяти после ответа. По умолчанию
        # Ollama хранит её пять минут — и всё это время распознавание
        # следующего созвона делит с ней карту и идёт втрое медленнее.
        "keep_alive": str(cfg_summary.get("keep_alive", "0s")),
        "options": {
            "num_ctx": int(cfg_summary["num_ctx"]),
            "temperature": float(cfg_summary["temperature"]),
        },
    }
    data = _post(cfg_summary["host"], "/api/chat", payload, int(cfg_summary["timeout_seconds"]))
    if "error" in data:
        raise OllamaError(str(data["error"]))
    return _strip_think(data.get("message", {}).get("content", ""))


def _template(name: str) -> str:
    return prompts.read(name)


def _chunks(text: str, size: int, overlap: int) -> list[str]:
    """Порезать транскрипт на куски по границам строк, с перекрытием."""
    if len(text) <= size:
        return [text]
    out: list[str] = []
    buf = ""
    for line in _lines(text, size - overlap if overlap < size else size):
        if buf and len(buf) + len(line) > size:
            out.append(buf)
            buf = buf[-overlap:] if overlap else ""
        buf += line
    if buf.strip():
        out.append(buf)
    return out


def _lines(text: str, limit: int) -> list[str]:
    """Строки транскрипта, ни одна из которых не длиннее куска.

    Реплики одного говорящего склеиваются, пока пауза между ними меньше
    `merge_gap`, поэтому доклад без вопросов становится одной строкой на
    пятнадцать минут — длиннее всего куска. Раньше такая строка проезжала
    проверку целиком: кусок вырастал вдесятеро, не помещался в окно контекста
    модели, и часть созвона молча не доходила до протокола.
    """
    out: list[str] = []
    for line in text.splitlines(keepends=True):
        while len(line) > limit:
            cut = _word_break(line, limit)
            out.append(line[:cut])
            line = line[cut:]
        if line:
            out.append(line)
    return out


def _word_break(line: str, limit: int) -> int:
    """Где разрезать длинную строку: по пробелу, а не посреди слова."""
    space = line.rfind(" ", limit // 2, limit)
    return space + 1 if space > 0 else limit


def summarize(transcript: str, meta: str, cfg, log=print) -> str:
    """Построить протокол. Длинные созвоны обрабатываются по частям (map-reduce)."""
    if not transcript.strip():
        # Пустая запись не является поводом просить языковую модель придумать созвон.
        raise EmptyTranscriptError(EMPTY_TRANSCRIPT_MESSAGE)
    s = cfg.summary
    parts = _chunks(transcript, int(s["chunk_chars"]), int(s["chunk_overlap_chars"]))
    if len(parts) == 1:
        log("Саммари: один проход…")
        return _generate(s, _template("summary_ru.md").format(meta=meta, transcript=transcript))

    log(f"Саммари: транскрипт длинный, обрабатываю по частям ({len(parts)})…")
    notes = []
    map_tpl = _template("map_ru.md")
    for i, part in enumerate(parts, 1):
        log(f"  часть {i}/{len(parts)}…")
        notes.append(
            f"### Часть {i}\n"
            + _generate(s, map_tpl.format(index=i, total=len(parts), transcript=part))
        )
    log("  свожу части в протокол…")
    return _generate(s, _template("reduce_ru.md").format(meta=meta, notes="\n\n".join(notes)))
