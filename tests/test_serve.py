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
    """Тесты не должны трогать видеокарту, грузить модель и качать FFmpeg.

    Про FFmpeg отдельно: на машине автопрогона его нет, и обработка честно
    полезла его скачивать — сто мегабайт посреди тестов. Своя докачка проверена
    в test_ffmpeg.py, здесь она только мешает.
    """
    monkeypatch.setattr(
        serve_module.Transcriber, "_resolve_device",
        staticmethod(lambda device, compute: ("cpu", "int8")),
    )
    monkeypatch.setattr(serve_module.ffmpeg, "missing", lambda folder=None: [])


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


def test_missing_recording_is_an_error(cfg, tmp_path):
    events = run(cfg, {"cmd": "process", "id": "7", "path": str(tmp_path / "нет-такого.mkv")})
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
    monkeypatch.setattr(
        serve_module.Engine, "_ensure_transcriber", lambda self, request_id=None: None)

    events = run(cfg, {"cmd": "process", "id": "9", "path": str(source)})
    kinds = [e["event"] for e in events]
    assert "progress" in kinds and "log" in kinds

    # Именно о распознавании: до него мотор может сообщить и о докачке.
    progress = next(
        e for e in events if e["event"] == "progress" and e["stage"] == "transcribe")
    assert (progress["fraction"], progress["detail"]) == (0.5, "Собеседник")

    done = next(e for e in events if e["event"] == "done")
    assert done["summary"] is True


def test_transcriber_explains_itself_in_the_window(cfg, tmp_path, monkeypatch):
    """Скачивание модели должно объясняться словами, а не одной полосой.

    Распознаватель говорит о скачивании, починке кэша и потерянной связи, но
    мотор создавал его молчащим: наружу пробивались только мегабайты под
    таймером, и человек не понимал, что происходит.
    """
    source = tmp_path / "rec" / "созвон.mkv"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"")

    class FakeTranscriber:
        def __init__(self, cfg_, verbose=True, on_progress=None, log=None):
            log("Скачиваю модель распознавания large-v3")
            self.device, self.compute_type = "cpu", "int8"

        @staticmethod
        def _resolve_device(device, compute):
            return "cpu", "int8"

    class FakeResult:
        def __init__(self, out_dir):
            self.out_dir = out_dir
            self.summary_md = out_dir / "summary.md"

    def fake_process(src, config_, transcriber=None, log=print, on_stage=None, **kwargs):
        out_dir = cfg.path("out") / "созвон"
        out_dir.mkdir(parents=True, exist_ok=True)
        return FakeResult(out_dir)

    monkeypatch.setattr(serve_module, "Transcriber", FakeTranscriber)
    monkeypatch.setattr(serve_module, "process", fake_process)

    events = run(cfg, {"cmd": "process", "id": "9", "path": str(source)})

    said = [e["text"] for e in events if e["event"] == "log"]
    assert any("Скачиваю модель распознавания" in line for line in said), said
    assert any("Модель загружена" in line for line in said), said


def test_transcriber_speaks_for_the_record_it_is_working_on(cfg, tmp_path, monkeypatch):
    """Строки распознавателя относятся к той записи, которую он сейчас разбирает.

    Распознаватель создаётся один раз и живёт до конца работы: модель грузится
    в видеопамять секунды. Вместе с ним запоминался и номер задания — номер
    первой записи, — поэтому всё сказанное про вторую и следующие уходило
    в окно под чужим номером.
    """
    sources = []
    for name in ("первый", "второй"):
        source = tmp_path / "rec" / f"{name}.mkv"
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(b"")
        sources.append(source)

    class FakeTranscriber:
        def __init__(self, cfg_, verbose=True, on_progress=None, log=None):
            self.say = log
            self.device, self.compute_type = "cpu", "int8"

        @staticmethod
        def _resolve_device(device, compute):
            return "cpu", "int8"

    class FakeResult:
        def __init__(self, out_dir):
            self.out_dir = out_dir
            self.summary_md = out_dir / "summary.md"

    def fake_process(src, config_, transcriber=None, log=print, on_stage=None, **kwargs):
        # Так о себе отчитывается настоящий: через свой log, а не через log пайплайна.
        transcriber.say(f"  [Я] готово: {src.stem}")
        out_dir = cfg.path("out") / src.stem
        out_dir.mkdir(parents=True, exist_ok=True)
        return FakeResult(out_dir)

    monkeypatch.setattr(serve_module, "Transcriber", FakeTranscriber)
    monkeypatch.setattr(serve_module, "process", fake_process)

    events = run(
        cfg,
        {"cmd": "process", "id": "1", "path": str(sources[0])},
        {"cmd": "process", "id": "2", "path": str(sources[1])},
    )

    said = {e["text"]: e["id"] for e in events if e["event"] == "log"}
    assert said["  [Я] готово: первый"] == "1"
    assert said["  [Я] готово: второй"] == "2", said


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


def test_settings_are_read_while_processing_is_running(cfg, monkeypatch):
    """Окно настроек не ждёт окончания долгой расшифровки."""
    import threading

    from callsum.serve import Engine

    started = threading.Event()
    release = threading.Event()
    events: list[dict] = []

    def slow_process(command):
        started.set()
        assert release.wait(5)

    engine = Engine(cfg, events.append)
    monkeypatch.setattr(engine, "_process", slow_process)
    engine.start()
    engine.submit({"cmd": "process", "id": "recording"})
    assert started.wait(5)

    engine.submit({"cmd": "settings", "id": "settings"})
    settings = next(event for event in events if event["event"] == "settings")
    assert settings["id"] == "settings"
    assert settings["values"]["paths"]["out"] == str(cfg.path("out"))

    release.set()
    engine.stop(timeout=5)


def test_prompts_are_read_while_processing_is_running(cfg, monkeypatch):
    """Редактор шаблонов не ждёт распознавания или запроса к Ollama."""
    import threading

    from callsum.serve import Engine

    started = threading.Event()
    release = threading.Event()
    events: list[dict] = []

    def slow_process(command):
        started.set()
        assert release.wait(5)

    engine = Engine(cfg, events.append)
    monkeypatch.setattr(engine, "_process", slow_process)
    engine.start()
    engine.submit({"cmd": "process", "id": "recording"})
    assert started.wait(5)

    engine.submit({"cmd": "prompts", "id": "prompts"})
    report = next(event for event in events if event["event"] == "prompts")
    assert report["id"] == "prompts"
    assert {item["name"] for item in report["items"]} == {
        "summary_ru.md", "map_ru.md", "reduce_ru.md",
    }

    release.set()
    engine.stop(timeout=5)


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


def test_byte_order_mark_does_not_break_a_command(cfg, monkeypatch):
    """PowerShell дописывает метку кодировки в начало потока — команда всё равно должна пройти."""
    calls: list[dict] = []
    monkeypatch.setattr(serve_module.Engine, "_run", lambda self, command: calls.append(command))

    out: list[str] = []
    serve(
        cfg,
        lines=["\ufeff" + json.dumps({"cmd": "doctor", "id": "1"}),
               json.dumps({"cmd": "shutdown"})],
        write=out.append,
    )

    assert [c["id"] for c in calls] == ["1"]
    assert not any("Не разобрал" in line for line in out)


def test_doctor_creates_working_folders(cfg):
    """Проверка окружения должна оставлять его готовым к работе."""
    recordings = cfg.path("recordings")
    out = cfg.path("out")
    assert not recordings.exists()

    events = run(cfg, {"cmd": "doctor", "id": "1"})
    report = next(e for e in events if e["event"] == "doctor")

    assert recordings.is_dir() and out.is_dir()
    assert report["recordings"] == str(recordings)
    assert report["out"] == str(out)


def test_doctor_tells_how_to_open_protocols(cfg):
    """Чем открывать протоколы, знает только ядро: настройка лежит в его config.toml."""
    cfg.view["markdown_app"] = "code -r {file}"

    events = run(cfg, {"cmd": "doctor", "id": "1"})
    report = next(e for e in events if e["event"] == "doctor")

    assert report["markdown_app"] == "code -r {file}"


def test_mangled_path_hints_at_the_console_encoding(cfg, tmp_path):
    """Кириллица, потерянная при передаче команды, — частая беда PowerShell."""
    events = run(cfg, {"cmd": "process", "id": "1", "path": str(tmp_path / "????????.mkv")})
    error = next(e for e in events if e["event"] == "error")
    assert "OutputEncoding" in error["message"]


def test_plain_missing_file_has_no_extra_advice(cfg, tmp_path):
    events = run(cfg, {"cmd": "process", "id": "1", "path": str(tmp_path / "absent.mkv")})
    error = next(e for e in events if e["event"] == "error")
    assert "OutputEncoding" not in error["message"]


def test_commands_are_read_line_by_line(monkeypatch):
    """Чтение stdin пачками подвешивало ядро: команда застревала в буфере.

    Приложение отправляет по одной строке и ждёт ответа, поэтому ядро обязано
    читать построчно, а не блоками по восемь килобайт.
    """
    import io

    from callsum.serve import _stdin_lines

    class LineByLine(io.StringIO):
        """Поток, который отдаёт строки только через readline."""

        def __iter__(self):
            raise AssertionError("чтение итерацией буферизуется — так нельзя")

    monkeypatch.setattr(serve_module.sys, "stdin", LineByLine("первая\nвторая\n"))

    assert list(_stdin_lines()) == ["первая\n", "вторая\n"]


# --- настройки -------------------------------------------------------------
def test_settings_are_reported_with_their_file(cfg, tmp_path):
    """Окно рисует форму по этим данным, поэтому в ответе и значения, и пути."""
    source = tmp_path / "config.toml"
    source.write_text("[paths]\nout = 'out'\n", encoding="utf-8")
    cfg.source = source

    events = run(cfg, {"cmd": "settings", "id": "1"})
    report = next(e for e in events if e["event"] == "settings")

    assert report["path"] == str(source)
    assert report["values"]["summary"]["model"] == cfg.summary["model"]
    # Относительный путь в файле — обычное дело: окну нужно показать, где
    # файлы окажутся на самом деле.
    assert report["resolved"]["out"] == str(cfg.path("out"))


def test_settings_are_written_and_applied(cfg, tmp_path):
    source = tmp_path / "config.toml"
    source.write_text("# папки\n[paths]\nout = 'out'\n", encoding="utf-8")
    cfg.source = source

    events = run(
        cfg,
        {"cmd": "settings_set", "id": "1", "values": {"paths": {"out": str(tmp_path / "готовое")}}},
        {"cmd": "settings", "id": "2"},
    )

    assert "# папки" in source.read_text(encoding="utf-8"), "комментарии должны остаться"
    report = [e for e in events if e["event"] == "settings"][-1]
    assert report["values"]["paths"]["out"] == str(tmp_path / "готовое")
    assert report["resolved"]["out"] == str(tmp_path / "готовое")


def test_previous_settings_are_kept_beside_the_new_ones(cfg, tmp_path):
    """Оборвись запись на середине — пользователь остался бы без настроек."""
    source = tmp_path / "config.toml"
    source.write_text("[paths]\nout = 'было'\n", encoding="utf-8")
    cfg.source = source

    run(cfg, {"cmd": "settings_set", "id": "1", "values": {"paths": {"out": "стало"}}})

    assert "было" in (tmp_path / "config.toml.bak").read_text(encoding="utf-8")
    assert "стало" in source.read_text(encoding="utf-8")


def test_unknown_settings_section_is_refused(cfg, tmp_path):
    cfg.source = tmp_path / "config.toml"

    events = run(cfg, {"cmd": "settings_set", "id": "1", "values": {"погода": {"дождь": True}}})

    error = next(e for e in events if e["event"] == "error")
    assert "погода" in error["message"]
    assert not (tmp_path / "config.toml").exists()


def test_settings_must_come_in_sections(cfg, tmp_path):
    cfg.source = tmp_path / "config.toml"

    events = run(cfg, {"cmd": "settings_set", "id": "1", "values": {"out": "D:/созвоны"}})

    assert any(e["event"] == "error" for e in events)


def test_user_prompt_can_be_saved_and_restored(cfg, tmp_path, monkeypatch):
    monkeypatch.setattr(serve_module.prompts.config, "config_dir", lambda: tmp_path / "config")
    changed = "Итог:\n{meta}\n{transcript}"

    events = run(
        cfg,
        {"cmd": "prompt_set", "id": "1", "name": "summary_ru.md", "content": changed},
        {"cmd": "prompt_reset", "id": "2", "name": "summary_ru.md"},
    )

    reports = [event for event in events if event["event"] == "prompts"]
    saved = next(item for item in reports[0]["items"] if item["name"] == "summary_ru.md")
    restored = next(item for item in reports[1]["items"] if item["name"] == "summary_ru.md")
    assert saved["content"] == changed and saved["custom"] is True
    assert restored["content"] != changed and restored["custom"] is False


def test_changed_model_drops_the_loaded_one(cfg, tmp_path, monkeypatch):
    """Модель уже в видеопамяти: после смены выбора её нужно перезагрузить."""
    source = tmp_path / "config.toml"
    source.write_text("[transcribe]\nmodel = 'large-v3'\n", encoding="utf-8")
    cfg.source = source

    released: list[str] = []

    class FakeTranscriber:
        def release(self):
            released.append("да")

    engine = serve_module.Engine(cfg, lambda message: None)
    engine._transcriber = FakeTranscriber()
    engine._run({"cmd": "settings_set", "id": "1", "values": {"transcribe": {"model": "medium"}}})

    assert released == ["да"]
    assert engine._transcriber is None


def test_settings_report_tells_the_default_folders(cfg, tmp_path):
    """Окно показывает подсказкой, куда программа сложила бы всё сама."""
    cfg.source = tmp_path / "config.toml"

    events = run(cfg, {"cmd": "settings", "id": "1"})
    report = next(e for e in events if e["event"] == "settings")

    from pathlib import Path as _Path

    for key in ("recordings", "out"):
        assert _Path(report["defaults"]["paths"][key]).is_absolute()
        assert report["defaults"]["paths"][key].endswith(key)


def test_defaults_come_for_every_setting(cfg, tmp_path):
    """Подсказка «по умолчанию» нужна у каждого поля, а не только у папок."""
    cfg.source = tmp_path / "config.toml"

    events = run(cfg, {"cmd": "settings", "id": "1"})
    report = next(e for e in events if e["event"] == "settings")

    defaults = report["defaults"]
    assert set(defaults) == set(config.DEFAULTS)
    assert defaults["transcribe"]["model"] == "large-v3"
    assert defaults["summary"]["num_ctx"] == 8192
    assert defaults["audio"]["extensions"][0] == ".mkv"
    # Подстановка полных путей не должна портить сами умолчания программы.
    assert config.DEFAULTS["paths"]["out"] == "out"


def test_installed_ollama_models_are_listed(cfg, monkeypatch):
    """Окно предлагает выбрать модель протокола из установленных."""
    asked: list[str] = []

    def fake(host, timeout=10):
        asked.append(host)
        return ["qwen3:14b", "llama3:8b"]

    monkeypatch.setattr(serve_module.summarize, "available_models", fake)

    events = run(cfg, {"cmd": "models", "id": "1", "host": "http://гость:11434"})

    answer = next(e for e in events if e["event"] == "models")
    assert answer["models"] == ["qwen3:14b", "llama3:8b"]
    # Адрес берётся из команды: в окне его меняют рядом со списком, и список
    # должен относиться к тому, что человек только что вписал.
    assert asked == ["http://гость:11434"]


def test_models_without_ollama_are_reported_as_error(cfg, monkeypatch):
    def fake(host, timeout=10):
        raise serve_module.summarize.OllamaError("Ollama не отвечает на http://x")

    monkeypatch.setattr(serve_module.summarize, "available_models", fake)

    events = run(cfg, {"cmd": "models", "id": "1"})

    error = next(e for e in events if e["event"] == "error")
    assert "Ollama" in error["message"]
    assert not any(e["event"] == "models" for e in events), "пустой список ввёл бы в заблуждение"


def test_doctor_tells_whether_the_protocol_is_wanted(cfg, tmp_path, monkeypatch):
    """С выключенным протоколом молчащая Ollama — не повод предупреждать."""
    monkeypatch.setattr(serve_module.summarize, "available_models", lambda host, timeout=10: [])
    cfg.data["summary"] = dict(cfg.summary, enabled=False, model="qwen3:14b")
    cfg.source = tmp_path / "config.toml"

    report = next(e for e in run(cfg, {"cmd": "doctor", "id": "1"}) if e["event"] == "doctor")

    assert report["summary_enabled"] is False
    # Имя модели нужно, чтобы окно могло сказать, что именно загружать.
    assert report["summary_model"] == "qwen3:14b"


def test_doctor_tells_where_things_are_kept(cfg, tmp_path, monkeypatch):
    """Вкладка «О программе» показывает эти пути: сама она их не знает."""
    monkeypatch.setattr(serve_module.summarize, "available_models", lambda host, timeout=10: [])
    cfg.source = tmp_path / "config.toml"

    events = run(cfg, {"cmd": "doctor", "id": "1"})
    report = next(e for e in events if e["event"] == "doctor")

    assert report["cuda_dir"].endswith("cuda")
    assert report["version"]
