"""Саммари транскрипта локальной моделью через Ollama."""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.request
from pathlib import Path

from .config import RESOURCES

PROMPTS = RESOURCES / "prompts"


class OllamaError(RuntimeError):
    pass


def _post(host: str, path: str, payload: dict, timeout: int) -> dict:
    req = urllib.request.Request(
        f"{host.rstrip('/')}{path}",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        raise OllamaError(
            f"Ollama не отвечает на {host}: {exc}. Проверьте, что сервис запущен (`ollama serve`)."
        ) from exc


def available_models(host: str, timeout: int = 10) -> list[str]:
    try:
        with urllib.request.urlopen(f"{host.rstrip('/')}/api/tags", timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        raise OllamaError(f"Ollama не отвечает на {host}: {exc}") from exc
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
    return (PROMPTS / name).read_text(encoding="utf-8")


def _chunks(text: str, size: int, overlap: int) -> list[str]:
    """Порезать транскрипт на куски по границам строк, с перекрытием."""
    if len(text) <= size:
        return [text]
    lines = text.splitlines(keepends=True)
    out: list[str] = []
    buf = ""
    for line in lines:
        if buf and len(buf) + len(line) > size:
            out.append(buf)
            buf = buf[-overlap:] if overlap else ""
        buf += line
    if buf.strip():
        out.append(buf)
    return out


def summarize(transcript: str, meta: str, cfg, log=print) -> str:
    """Построить протокол. Длинные созвоны обрабатываются по частям (map-reduce)."""
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
