"""Тест защиты от повторной обработки одной и той же записи."""

import copy

from callsum import config
from callsum.gui import Worker


def _cfg(tmp_path):
    data = copy.deepcopy(config.DEFAULTS)
    data["paths"] = {"recordings": str(tmp_path / "rec"), "out": str(tmp_path / "out")}
    return config.Config(data)


def test_already_processed_recording_is_skipped(tmp_path):
    """Два запущенных окна получают одно событие OBS — обрабатывать дважды незачем."""
    cfg = _cfg(tmp_path)
    out = cfg.path("out") / "созвон"
    out.mkdir(parents=True)
    (out / "transcript.md").write_text("готово", encoding="utf-8")

    worker = Worker(cfg)
    messages, finished = [], []
    worker.message.connect(messages.append)
    worker.done.connect(lambda *args: finished.append(args))

    worker.handle(str(tmp_path / "rec" / "созвон.mkv"), False)

    assert worker._transcriber is None, "модель не должна грузиться ради пропуска"
    assert any("уже обработан" in m for m in messages)
    assert finished == [("созвон", str(out), False)]


def test_manual_run_processes_even_if_result_exists(tmp_path):
    """Кнопка «Обработать файл…» — явная просьба, её пропускать нельзя."""
    cfg = _cfg(tmp_path)
    out = cfg.path("out") / "созвон"
    out.mkdir(parents=True)
    (out / "transcript.md").write_text("готово", encoding="utf-8")

    worker = Worker(cfg)
    failures = []
    worker.failed.connect(lambda name, error: failures.append(name))

    # Файла записи нет, поэтому обработка честно падает — важно, что она началась.
    worker.handle(str(tmp_path / "rec" / "созвон.mkv"), True)
    assert failures == ["созвон.mkv"]
