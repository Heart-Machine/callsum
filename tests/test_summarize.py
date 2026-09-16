"""Протокол через Ollama: отказы должны приходить понятной ошибкой, а не любой."""

import json
import urllib.error

import pytest

from callsum import summarize


@pytest.fixture
def block():
    """Раздел [summary] в том виде, в каком его читает _generate."""
    return {
        "model": "qwen3:14b",
        "host": "http://127.0.0.1:11434",
        "num_ctx": 8192,
        "temperature": 0.2,
        "think": False,
        "keep_alive": "0s",
        "timeout_seconds": 60,
    }


def _falls(exception):
    def instead(*args, **kwargs):
        raise exception

    return instead


def _http_error(code: int, body: dict) -> urllib.error.HTTPError:
    import io

    return urllib.error.HTTPError(
        "http://127.0.0.1:11434/api/chat", code, "Not Found", {},
        io.BytesIO(json.dumps(body).encode("utf-8")),
    )


def test_slow_answer_is_an_ollama_error(monkeypatch, block):
    """Ответ, оборванный по времени, приходит голым TimeoutError — не URLError.

    Пайплайн ловит только OllamaError: пропусти он эту ошибку мимо, готовая
    расшифровка пропала бы под надписью «Ошибка обработки».
    """
    monkeypatch.setattr(
        summarize.urllib.request, "urlopen", _falls(TimeoutError("timed out")))

    with pytest.raises(summarize.OllamaError) as failure:
        summarize._generate(block, "текст")

    assert "timeout_seconds" in str(failure.value), "надо сказать, что делать"


def test_dropped_connection_is_an_ollama_error(monkeypatch, block):
    """Ollama, перезапущенная посреди ответа, рвёт соединение обычным OSError."""
    monkeypatch.setattr(
        summarize.urllib.request, "urlopen",
        _falls(ConnectionResetError("соединение разорвано")))

    with pytest.raises(summarize.OllamaError):
        summarize._generate(block, "текст")


def test_answer_that_is_not_json_is_an_ollama_error(monkeypatch, block):
    """На этом порту может отвечать и не Ollama — тогда приходит не JSON."""

    class Page:
        def read(self):
            return b"<html>proxy</html>"

        def __enter__(self):
            return self

        def __exit__(self, *args):
            return False

    monkeypatch.setattr(summarize.urllib.request, "urlopen", lambda *a, **k: Page())

    with pytest.raises(summarize.OllamaError):
        summarize._generate(block, "текст")


def test_missing_model_says_what_to_load(monkeypatch, block):
    """404 от Ollama означает «нет такой модели», а не «сервис не запущен».

    Прежнее сообщение советовало запустить `ollama serve` — а она работала.
    """
    monkeypatch.setattr(
        summarize.urllib.request, "urlopen",
        _falls(_http_error(404, {"error": "model 'qwen3:14b' not found"})))

    with pytest.raises(summarize.OllamaError) as failure:
        summarize._generate(block, "текст")

    said = str(failure.value)
    assert "ollama pull qwen3:14b" in said
    assert "serve" not in said, "сервис запущен — совет запустить его только путает"


def test_model_list_survives_a_timeout(monkeypatch):
    """Список моделей спрашивает проверка окружения: её ответ ждёт окно.

    Пропущенная мимо ошибка оставляла окно без путей и без замечаний вовсе.
    """
    monkeypatch.setattr(
        summarize.urllib.request, "urlopen", _falls(TimeoutError("timed out")))

    with pytest.raises(summarize.OllamaError):
        summarize.available_models("http://127.0.0.1:11434")


def test_empty_transcript_is_never_sent_to_ollama(monkeypatch):
    """Модель на пустом вводе уверенно придумывает несуществующий созвон."""
    monkeypatch.setattr(
        summarize, "_generate", lambda *_args: pytest.fail("Ollama не должна вызываться"))

    with pytest.raises(summarize.EmptyTranscriptError) as failure:
        summarize.summarize("", "- Файл: без речи.mkv", None)

    assert "нет распознанной речи" in str(failure.value)


def test_empty_dialog_does_not_turn_document_metadata_into_speech():
    raw = "# Расшифровка\n\n- Файл: без речи.mkv\n\n---\n\n"

    assert summarize.dialog_from_document(raw) == ""


def test_long_reply_is_cut_to_fit_the_chunk():
    """Один говорящий без пауз даёт одну реплику — длиннее куска целиком.

    Реплики склеиваются, пока пауза меньше merge_gap, поэтому пятнадцать минут
    монолога становятся одной строкой. Кусок из неё перерастал окно контекста,
    и часть созвона молча не доезжала до модели.
    """
    monologue = " ".join(["слово"] * 4000) + "\n"
    text = "[00:00:00] Я: " + monologue + "[00:20:00] Собеседник: коротко\n"

    parts = summarize._chunks(text, 1000, 100)

    assert all(len(part) <= 1000 for part in parts), [len(p) for p in parts]
    assert "коротко" in parts[-1]


def test_cutting_a_long_reply_does_not_break_words():
    """Резать посреди слова незачем: модели достанется мусор на стыке."""
    parts = summarize._chunks("Я: " + " ".join(["слово"] * 500) + "\n", 300, 50)

    assert len(parts) > 1, "одна строка на 3000 символов обязана разрезаться"
    for part in parts:
        assert part.strip().split()[-1] in ("слово", "Я:"), part[-40:]
