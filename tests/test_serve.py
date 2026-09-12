"""Тесты протокола мотора: команды в строках JSON, события в ответ."""

import copy
import json

import pytest

from callsum import config, serve as serve_module
from callsum.serve import serve


@pytest.fixture
def cfg(tmp_path):
    data = copy.deepcopy(config.DEFAULTS)
    data["paths"] = {
        "recordings": str(tmp_path / "rec"),
        "out": str(tmp_path / "out"),
        "folder_template": "{name}",
    }
    return config.Config(data)


@pytest.fixture(autouse=True)
def no_gpu_probe(monkeypatch):
    """Тесты не должны трогать видеокарту и грузить модель."""
    monkeypatch.setattr(
        serve_module.Transcriber, "_resolve_device",
        staticmethod(lambda device, compute: ("cpu", "int8")),
    )


def run(cfg, *commands):
    """Прогнать команды через мотор и вернуть разобранные события."""
    lines = [json.dumps(command, ensure_ascii=False) for command in commands]
    lines.append(json.dumps({"cmd": "shutdown"}))
    out: list[str] = []
    serve(cfg, lines=lines, write=out.append)
    return [json.loads(line) for line in out]


def test_ready_event_tells_how_the_engine_will_work(cfg):
    events = run(cfg)
    assert events[0]["event"] == "ready"
    assert events[0]["device"] == "cpu"
    assert events[0]["compute_type"] == "int8"
    assert events[0]["version"]


def test_unknown_command_is_reported_without_stopping(cfg):
    events = run(cfg, {"cmd": "полетели", "id": "1"}, {"cmd": "doctor", "id": "2"})
    errors = [e for e in events if e["event"] == "error"]
    assert "полетели" in errors[0]["message"]
    assert any(e["event"] == "doctor" for e in events), "мотор должен продолжить работу"


def test_broken_json_does_not_break_the_loop(cfg):
    out: list[str] = []
    serve(cfg, lines=['{не json', json.dumps({"cmd": "shutdown"})], write=out.append)
    events = [json.loads(line) for line in out]
    assert any(e["event"] == "error" and "разобрал" in e["message"] for e in events)


def test_missing_recording_is_an_error(cfg):
    events = run(cfg, {"cmd": "process", "id": "7", "path": "D:/нет-такого.mkv"})
    error = next(e for e in events if e["event"] == "error")
    assert error["id"] == "7"
    assert "Нет файла" in error["message"]


def test_already_processed_recording_is_skipped(cfg, tmp_path, monkeypatch):
    """Повторная обработка не должна ни грузить модель, ни трогать пайплайн."""
    source = tmp_path / "rec" / "созвон.mkv"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"")
    result = tmp_path / "out" / "созвон"
    result.mkdir(parents=True)
    (result / "transcript.md").write_text("готово", encoding="utf-8")

    def refuse(*args, **kwargs):
        raise AssertionError("пайплайн не должен запускаться")

    monkeypatch.setattr(serve_module, "process", refuse)

    events = run(cfg, {"cmd": "process", "id": "3", "path": str(source)})
    done = next(e for e in events if e["event"] == "done")
    assert done["id"] == "3"
    assert done["out_dir"] == str(result)
    assert done["summary"] is False


def test_processing_reports_progress_and_result(cfg, tmp_path, monkeypatch):
    source = tmp_path / "rec" / "созвон.mkv"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"")

    class FakeResult:
        def __init__(self, out_dir):
            self.out_dir = out_dir
            self.summary_md = out_dir / "summary.md"

    def fake_process(src, config_, transcriber=None, log=print, on_stage=None, **kwargs):
        out_dir = cfg.path("out") / "созвон"
        out_dir.mkdir(parents=True, exist_ok=True)
        (out_dir / "summary.md").write_text("протокол", encoding="utf-8")
        log("дорожка 2 пустая — пропускаю")
        on_stage("transcribe", 0.5, "Собеседник")
        return FakeResult(out_dir)

    monkeypatch.setattr(serve_module, "process", fake_process)
    monkeypatch.setattr(serve_module.Engine, "_ensure_transcriber", lambda self: None)

    events = run(cfg, {"cmd": "process", "id": "9", "path": str(source)})
    kinds = [e["event"] for e in events]
    assert "progress" in kinds and "log" in kinds

    progress = next(e for e in events if e["event"] == "progress")
    assert (progress["stage"], progress["fraction"], progress["detail"]) == (
        "transcribe", 0.5, "Собеседник")

    done = next(e for e in events if e["event"] == "done")
    assert done["summary"] is True


def test_summarize_without_transcript_is_an_error(cfg, tmp_path):
    events = run(cfg, {"cmd": "summarize", "id": "4", "transcript": str(tmp_path / "нет")})
    error = next(e for e in events if e["event"] == "error")
    assert "Нет расшифровки" in error["message"]


def test_every_event_is_one_line_of_json(cfg):
    """Приложение читает вывод построчно — перевод строки внутри сломал бы разбор."""
    out: list[str] = []
    serve(cfg, lines=[json.dumps({"cmd": "doctor", "id": "1"}),
                      json.dumps({"cmd": "shutdown"})], write=out.append)
    assert out and all("\n" not in line for line in out)


def test_queued_work_finishes_before_shutdown(cfg, monkeypatch):
    """Принятые команды выполняются: молча терять работу приложение не должно."""
    import threading

    from callsum.serve import Engine

    done: list[str] = []
    events: list[dict] = []

    def slow(command):
        threading.Event().wait(0.05)
        done.append(command["id"])

    engine = Engine(cfg, events.append)
    monkeypatch.setattr(engine, "_run", slow)
    engine.start()
    engine.submit({"cmd": "process", "id": "первый"})
    engine.submit({"cmd": "process", "id": "второй"})
    engine.stop(timeout=5)

    assert done == ["первый", "второй"]


def test_commands_sent_before_shutdown_are_executed(cfg, monkeypatch):
    """shutdown приходит сразу за командой — она всё равно должна выполниться."""
    calls: list[dict] = []
    monkeypatch.setattr(serve_module.Engine, "_run", lambda self, command: calls.append(command))

    run(cfg, {"cmd": "doctor", "id": "1"}, {"cmd": "doctor", "id": "2"})

    assert [c["id"] for c in calls] == ["1", "2"]


def test_stray_print_does_not_corrupt_the_protocol(cfg, monkeypatch, capsys):
    """Печать из любого места программы не должна попадать в поток событий."""
    import sys as sys_module

    def noisy(self, command):
        print("посторонний вывод, который сломал бы разбор")
        sys_module.stdout.write("и ещё один\n")
        self.emit({"event": "log", "id": command.get("id"), "text": "нормальное событие"})

    monkeypatch.setattr(serve_module.Engine, "_run", noisy)

    out: list[str] = []
    serve(cfg, lines=[json.dumps({"cmd": "doctor", "id": "1"}),
                      json.dumps({"cmd": "shutdown"})], write=out.append)

    events = [json.loads(line) for line in out]      # всё разбирается как JSON
    assert any(e.get("text") == "нормальное событие" for e in events)
    assert "посторонний вывод" in capsys.readouterr().err


def test_console_streams_are_utf8(monkeypatch):
    """Путь с кириллицей приходит в stdin — читать его в кодировке консоли нельзя."""
    from callsum import cli

    class Stream:
        def __init__(self):
            self.encoding = "cp1251"

        def reconfigure(self, encoding):
            self.encoding = encoding

    streams = {name: Stream() for name in ("stdout", "stderr", "stdin")}
    for name, stream in streams.items():
        monkeypatch.setattr(cli.sys, name, stream)

    cli._utf8_console()

    assert [s.encoding for s in streams.values()] == ["utf-8"] * 3
