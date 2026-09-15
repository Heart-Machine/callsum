"""Watcher продолжает работу после временных ошибок одной записи."""

import copy

import pytest

from callsum import config, naming, watch


class StopWatching(Exception):
    pass


def test_failed_recording_is_retried_on_the_next_scan(tmp_path, monkeypatch):
    data = copy.deepcopy(config.DEFAULTS)
    data["paths"] = {
        "recordings": str(tmp_path / "recordings"),
        "out": str(tmp_path / "out"),
        "folder_template": "{name}",
    }
    data["audio"]["stable_seconds"] = 0
    cfg = config.Config(data)
    source = cfg.path("recordings") / "созвон.mkv"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"audio")
    attempts: list[str] = []

    class FakeTranscriber:
        def __init__(self, _cfg):
            pass

    def fake_process(src, cfg, **_kwargs):
        attempts.append(src.name)
        if len(attempts) == 1:
            raise RuntimeError("модель ещё скачивается")
        out_dir = naming.result_dir(src, cfg.path("out"), cfg.paths["folder_template"])
        out_dir.mkdir(parents=True)
        (out_dir / "transcript.md").write_text("готово", encoding="utf-8")

    def stop_after_retry(_interval):
        if len(attempts) >= 2:
            raise StopWatching

    monkeypatch.setattr(watch, "Transcriber", FakeTranscriber)
    monkeypatch.setattr(watch, "process", fake_process)
    monkeypatch.setattr(watch.time, "sleep", stop_after_retry)

    with pytest.raises(StopWatching):
        watch.run(cfg, interval=0, log=lambda _line: None)

    assert attempts == ["созвон.mkv", "созвон.mkv"]
