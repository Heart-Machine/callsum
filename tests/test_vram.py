"""Тесты освобождения видеопамяти между распознаванием и протоколом."""

import copy

import pytest

from callsum import config, summarize
from callsum.transcribe import Transcriber


class FakeCTranslate2Model:
    """Подставная модель CTranslate2: помнит, где лежат её веса."""

    def __init__(self):
        self.model_is_loaded = True
        self.unloaded_to_cpu = None
        self.loads = 0

    def unload_model(self, to_cpu=False):
        self.model_is_loaded = False
        self.unloaded_to_cpu = to_cpu

    def load_model(self, keep_cache=False):
        self.model_is_loaded = True
        self.loads += 1


class FakeWhisperModel:
    def __init__(self):
        self.model = FakeCTranslate2Model()

    def transcribe(self, *args, **kwargs):
        return iter(()), type("Info", (), {"duration": 0.0})()


@pytest.fixture
def transcriber():
    """Собираем Transcriber в обход конструктора: модель нам нужна подставная."""
    instance = Transcriber.__new__(Transcriber)
    instance.cfg = config.Config(copy.deepcopy(config.DEFAULTS))
    instance.verbose = False
    instance.device = "cuda"
    instance.compute_type = "float16"
    instance.model = FakeWhisperModel()
    return instance


def test_release_moves_weights_out_of_video_memory(transcriber):
    """Пока модель распознавания в видеопамяти, языковой модели не хватает места."""
    transcriber.release()

    assert transcriber.model.model.model_is_loaded is False
    assert transcriber.model.model.unloaded_to_cpu is True, "веса должны остаться в обычной памяти"


def test_release_is_safe_to_call_twice(transcriber):
    transcriber.release()
    transcriber.release()
    assert transcriber.model.model.model_is_loaded is False


def test_next_recording_loads_the_model_back(transcriber, tmp_path):
    transcriber.release()
    transcriber.run(tmp_path / "запись.wav", "Я", 0.0)

    assert transcriber.model.model.model_is_loaded is True
    assert transcriber.model.model.loads == 1, "загрузка идёт из памяти, а не с диска"


def test_summary_request_asks_to_free_video_memory():
    """Модель Ollama не должна занимать карту, пока распознаётся следующий созвон."""
    sent = {}

    def fake_post(host, path, payload, timeout):
        sent.update(payload)
        return {"message": {"content": "протокол"}}

    original = summarize._post
    summarize._post = fake_post
    try:
        summarize._generate(config.DEFAULTS["summary"], "текст")
    finally:
        summarize._post = original

    assert sent["keep_alive"] == "0s"
